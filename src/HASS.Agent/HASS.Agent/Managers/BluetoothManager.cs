using Windows.Devices.Bluetooth;
using Serilog;
using Windows.Devices.Enumeration;
using BluetoothDevice = HASS.Agent.Models.Internal.BluetoothDevice;
using WindowsBluetoothDevice = Windows.Devices.Bluetooth.BluetoothDevice;
using Windows.Devices.Bluetooth.Advertisement;
using HASS.Agent.Models.Internal;
using System.Threading.Tasks;

namespace HASS.Agent.Managers
{
    internal static class BluetoothManager
    {
        private static readonly SemaphoreSlim Semaphore = new(1, 1);

        private static BluetoothLEAdvertisementWatcher _leWatcher;
        // keyed by BluetoothAddress (ulong) for O(1) lookup without calling FromBluetoothAddressAsync
        private static readonly Dictionary<ulong, BluetoothLeDevice> DetectedLeDevices = new();
        private static bool _isWatchingLeDevices;

        /// <summary>
        /// Gets all known (connected) bluetooth devices
        /// </summary>
        /// <returns></returns>
        internal static async Task<List<BluetoothDevice>> GetDevicesAsync()
        {
            try
            {
                // first get all paired devices
                var pairedBluetoothDevices = await DeviceInformation.FindAllAsync(WindowsBluetoothDevice.GetDeviceSelectorFromPairingState(true));
                var devices = pairedBluetoothDevices.Select(pairedDevice => new BluetoothDevice { Id = pairedDevice.Id, Name = pairedDevice.Name, Paired = pairedDevice.Pairing.IsPaired, Connected = false, Kind = pairedDevice.Kind.ToString(), LastSeenUtc = DateTime.UtcNow}).ToList();

                // then get all connected devices
                var connectedBluetoothDevices = await DeviceInformation.FindAllAsync(WindowsBluetoothDevice.GetDeviceSelectorFromConnectionStatus(BluetoothConnectionStatus.Connected));
                foreach (var connectedDevice in connectedBluetoothDevices)
                {
                    // do we have it already?
                    if (devices.Any(x => x.Id == connectedDevice.Id))
                    {
                        // set it to connected
                        devices.Find(x => x.Id == connectedDevice.Id)!.Connected = true;
                        continue;
                    }

                    // add new one
                    var device = new BluetoothDevice
                    {
                        Id = connectedDevice.Id,
                        Name = connectedDevice.Name,
                        Paired = connectedDevice.Pairing.IsPaired,
                        Connected = true
                    };

                    devices.Add(device);
                }

                // done
                return devices;
            }
            catch (Exception ex)
            {
                Log.Fatal(ex, "[BLUETOOTH] Error getting devices: {err}", ex.Message);
                return new List<BluetoothDevice>();
            }
        }

        /// <summary>
        /// Initialises the LE scanner
        /// </summary>
        internal static void StartWatchingForLeDevices()
        {
            if (_isWatchingLeDevices) return;

            try
            {
                _isWatchingLeDevices = true;

                _leWatcher = new BluetoothLEAdvertisementWatcher
                {
                    // passive mode avoids sending scan-request packets to every advertiser,
                    // which was causing the Windows Device Association Service to consume
                    // constant CPU even when all detected devices were already known
                    ScanningMode = BluetoothLEScanningMode.Passive
                };

                _leWatcher.Received += ScanOnReceived;
                _leWatcher.Stopped += ScanOnStopped;

                _leWatcher.Start();

                Log.Information("[BLUETOOTH] LE scanner started");
            }
            catch (Exception ex)
            {
                Log.Fatal(ex, "[BLUETOOTH] Error starting LE scanner: {err}", ex.Message);
            }
        }

        /// <summary>
        /// Stops the LE scanner
        /// </summary>
        internal static void StopLeScan()
        {
            if (!_isWatchingLeDevices) return;

            try
            {
                _isWatchingLeDevices = false;
                _leWatcher.Stop();
            }
            catch (Exception ex)
            {
                Log.Fatal(ex, "[BLUETOOTH] Error stopping LE scanner: {err}", ex.Message);
            }
        }

        /// <summary>
        /// Retrieves a list of all detected LE devices since the last retrieval (unless clearList is set to false)
        /// </summary>
        /// <param name="clearList"></param>
        /// <returns></returns>
        internal static async Task<List<BluetoothLeDevice>> GetDetectedLeDevicesAsync(bool clearList = true)
        {
            try
            {
                // wait for the semaphore
                if (!await Semaphore.WaitAsync(TimeSpan.FromSeconds(5)))
                    return new List<BluetoothLeDevice>();

                try
                {
                    // make a copy of the devices
                    var deviceList = DetectedLeDevices.Values.ToList();

                    // if requested, clear the current list
                    if (clearList) DetectedLeDevices.Clear();

                    // done
                    return deviceList;
                }
                finally
                {
                    Semaphore.Release();
                }
            }
            catch (Exception ex)
            {
                Log.Fatal(ex, "[BLUETOOTH] Error retrieving LE devices: {err}", ex.Message);
                return new List<BluetoothLeDevice>();
            }
        }

        private static void ScanOnStopped(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementWatcherStoppedEventArgs args)
        {
            Log.Information("[BLUETOOTH] LE scanner stopped");
        }

        private static async void ScanOnReceived(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementReceivedEventArgs args)
        {
            try
            {
                // fast path: if we already know this address just update LastSeenUtc,
                // avoiding the expensive FromBluetoothAddressAsync call to the
                // Windows Device Association Service that was the root cause of
                // constant high CPU usage
                if (!await Semaphore.WaitAsync(TimeSpan.FromSeconds(5)))
                    return;

                try
                {
                    if (DetectedLeDevices.TryGetValue(args.BluetoothAddress, out var knownDevice))
                    {
                        knownDevice.LastSeenUtc = DateTime.UtcNow;
                        return;
                    }
                }
                finally
                {
                    Semaphore.Release();
                }

                // slow path: first time we've seen this address — resolve via Windows API
                using var device = await BluetoothLEDevice.FromBluetoothAddressAsync(args.BluetoothAddress);
                if (device == null) return;

                var leDevice = new BluetoothLeDevice
                {
                    Id = device.DeviceId,
                    Name = device.Name,
                    Connected = device.ConnectionStatus == BluetoothConnectionStatus.Connected,
                    LastSeenUtc = DateTime.UtcNow
                };

                if (!await Semaphore.WaitAsync(TimeSpan.FromSeconds(5)))
                    return;

                try
                {
                    // another concurrent call may have added this address while we were
                    // awaiting FromBluetoothAddressAsync, so use indexer (idempotent)
                    if (DetectedLeDevices.TryGetValue(args.BluetoothAddress, out var race))
                        race.LastSeenUtc = DateTime.UtcNow;
                    else
                        DetectedLeDevices[args.BluetoothAddress] = leDevice;
                }
                finally
                {
                    Semaphore.Release();
                }
            }
            catch (Exception ex)
            {
                Log.Fatal(ex, "[BLUETOOTH] Error processing LE device: {err}", ex.Message);
            }
        }
    }
}
