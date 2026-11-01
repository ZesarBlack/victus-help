using GHelper.Helpers;
using System.Management;

namespace GHelper.Msi
{
    // MSI platform WMI interface (root\wmi : MSI_ACPI), same one used by the Linux msi-wmi-platform driver.
    // Every method takes and returns a 32 byte package. Only read (Get_*) methods are used here.
    public static class MsiWmi
    {
        const string Namespace = @"root\wmi";
        const int PackageSize = 32;

        static readonly string[] DumpMethods =
        [
            "Get_EC", "Get_BIOS", "Get_Fan", "Get_Temperature", "Get_Thermal",
            "Get_Device", "Get_Power", "Get_AP", "Get_WMI",
        ];

        static readonly object _lock = new();
        static ManagementObject? _acpi;
        static bool _unavailable;
        static readonly HashSet<string> _unsupported = new();

        static int[]? _fans;
        static long _fansTime;

        static ManagementObject? GetAcpi()
        {
            if (_acpi is not null || _unavailable) return _acpi;

            try
            {
                using var searcher = new ManagementObjectSearcher(Namespace, "SELECT * FROM MSI_ACPI");
                _acpi = searcher.Get().Cast<ManagementObject>().FirstOrDefault();
                if (_acpi is null) _unavailable = true;
                Logger.WriteLine("MSI WMI: " + (_acpi is null ? "not found" : _acpi["InstanceName"]));
            }
            catch (Exception ex)
            {
                _unavailable = true;
                Logger.WriteLine("MSI WMI: " + ex.Message);
            }

            return _acpi;
        }

        public static bool IsAvailable()
        {
            lock (_lock) return GetAcpi() is not null;
        }

        static byte[]? Call(string method, byte[]? input = null)
        {
            lock (_lock)
            {
                if (_unsupported.Contains(method)) return null;

                var acpi = GetAcpi();
                if (acpi is null) return null;

                try
                {
                    byte[] bytes = new byte[PackageSize];
                    input?.CopyTo(bytes, 0);

                    using var inParams = acpi.GetMethodParameters(method);
                    if (inParams is not null)
                    {
                        // Parameter is an embedded Package_32 object with a Bytes array
                        foreach (PropertyData prop in inParams.Properties)
                        {
                            if (prop.Type != CimType.Object) continue;
                            string cimType = prop.Qualifiers["CIMTYPE"].Value.ToString() ?? "";
                            using var packageClass = new ManagementClass(Namespace, cimType.Replace("object:", ""), null);
                            var package = packageClass.CreateInstance();
                            package["Bytes"] = bytes;
                            inParams[prop.Name] = package;
                        }
                    }

                    using var outParams = acpi.InvokeMethod(method, inParams, null);
                    foreach (PropertyData prop in outParams.Properties)
                    {
                        if (prop.Value is ManagementBaseObject package && package["Bytes"] is byte[] result)
                            return result;
                    }

                    _unsupported.Add(method);
                    Logger.WriteLine($"MSI WMI {method}: no output");
                    return null;
                }
                catch (ManagementException ex) when (ex.ErrorCode == ManagementStatus.AccessDenied)
                {
                    _unavailable = true;
                    Logger.WriteLine("MSI WMI: access denied, run as administrator");
                    return null;
                }
                catch (ManagementException ex) when (ex.ErrorCode is ManagementStatus.MethodNotImplemented or ManagementStatus.InvalidMethod or ManagementStatus.NotSupported)
                {
                    _unsupported.Add(method);
                    Logger.WriteLine($"MSI WMI {method}: {ex.ErrorCode}");
                    return null;
                }
                catch (Exception ex)
                {
                    // Transient, retry on next call
                    Logger.WriteLine($"MSI WMI {method}: {ex.Message}");
                    return null;
                }
            }
        }

        // Fan tachometers: big endian u16 per fan starting at byte 1, RPM = 480000 / value
        static int[]? ReadFans()
        {
            var data = Call("Get_Fan");
            if (data is null || data.Length < 5) return null;

            int[] fans = new int[2];
            for (int i = 0; i < fans.Length; i++)
            {
                int value = (data[1 + i * 2] << 8) | data[2 + i * 2];
                fans[i] = value == 0 ? 0 : 480000 / value;
            }
            return fans;
        }

        // Fan speed in 100 RPM units, same as AsusACPI.GetFan
        public static int GetFan(AsusFan device)
        {
            if (device != AsusFan.CPU && device != AsusFan.GPU) return -1;

            long now = Environment.TickCount64;
            if (_fans is null || now - _fansTime > 1000)
            {
                _fans = ReadFans();
                _fansTime = now;
            }

            if (_fans is null) return -1;
            return _fans[(int)device] / 100;
        }

        // Logs raw output of all read methods, to map temperatures / modes on new models
        public static void Dump()
        {
            Logger.WriteLine($"MSI Dump: {AppConfig.GetModel()} BIOS {AppConfig.GetBiosAndModel()}");
            foreach (var method in DumpMethods)
            {
                var data = Call(method);
                Logger.WriteLine($"MSI {method}: " + (data is null ? "-" : BitConverter.ToString(data)));
            }
        }
    }
}
