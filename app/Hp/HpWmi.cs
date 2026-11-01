using GHelper.Helpers;
using System.Management;

namespace GHelper.Hp
{
    // HP BIOS WMI interface (root\wmi : hpqBIntM), used by Victus / OMEN laptops
    // Command codes match the Linux hp-wmi driver and OmenMon
    public static class HpWmi
    {
        const string Namespace = @"root\wmi";

        const uint CommandDefault = 0x20008;

        const uint TypeFanSpeed = 0x11;       // per-fan RPM (older Omen / Victus)
        const uint TypeThermalProfile = 0x1A; // set thermal / performance profile
        const uint TypeGetGpuPower = 0x21;    // GPU cTGP / PPAB / D-state
        const uint TypeSetGpuPower = 0x22;
        const uint TypeTemp = 0x23;           // CPU / system temperature in °C
        const uint TypeFanLevel = 0x2D;       // Victus S: all fans in 100 RPM units
        const uint TypeSetFanLevel = 0x2E;    // force fan speeds in 100 RPM units, BIOS reverts after ~2 min

        // Victus S thermal profiles
        const byte ProfileDefault = 0x00;
        const byte ProfilePerformance = 0x01;

        static readonly byte[] Sign = { 0x53, 0x45, 0x43, 0x55 }; // "SECU"

        static readonly object _lock = new();
        static ManagementObject? _bios;
        static bool _unavailable;
        static readonly HashSet<uint> _unsupported = new();

        static int[]? _fans;
        static long _fansTime;

        static ManagementObject? GetBios()
        {
            if (_bios is not null || _unavailable) return _bios;

            try
            {
                using var searcher = new ManagementObjectSearcher(Namespace, "SELECT * FROM hpqBIntM");
                _bios = searcher.Get().Cast<ManagementObject>().FirstOrDefault();
                if (_bios is null) _unavailable = true;
                Logger.WriteLine("HP WMI: " + (_bios is null ? "not found" : _bios["InstanceName"]));
            }
            catch (Exception ex)
            {
                _unavailable = true;
                Logger.WriteLine("HP WMI: " + ex.Message);
            }

            return _bios;
        }

        public static bool IsAvailable()
        {
            lock (_lock) return GetBios() is not null;
        }

        static byte[]? Call(uint commandType, byte[] input, int outSize, uint command = CommandDefault)
        {
            lock (_lock)
            {
                if (_unsupported.Contains(commandType)) return null;

                var bios = GetBios();
                if (bios is null) return null;

                try
                {
                    using var dataInClass = new ManagementClass(Namespace, "hpqBDataIn", null);
                    using var dataIn = dataInClass.CreateInstance();
                    dataIn["Sign"] = Sign;
                    dataIn["Command"] = command;
                    dataIn["CommandType"] = commandType;
                    dataIn["Size"] = (uint)input.Length;
                    dataIn["hpqBData"] = input;

                    using var inParams = bios.GetMethodParameters("hpqBIOSInt" + outSize);
                    inParams["InData"] = dataIn;

                    using var outParams = bios.InvokeMethod("hpqBIOSInt" + outSize, inParams, null);
                    var dataOut = (ManagementBaseObject)outParams["OutData"];

                    int code = Convert.ToInt32(dataOut["rwReturnCode"]);
                    if (code != 0)
                    {
                        _unsupported.Add(commandType);
                        Logger.WriteLine($"HP WMI 0x{commandType:X2}: return code {code}");
                        return null;
                    }

                    return outSize > 0 ? (byte[])dataOut["Data"] : Array.Empty<byte>();
                }
                catch (ManagementException ex) when (ex.ErrorCode == ManagementStatus.AccessDenied)
                {
                    _unavailable = true;
                    Logger.WriteLine("HP WMI: access denied, run as administrator");
                    return null;
                }
                catch (Exception ex)
                {
                    // Transient (e.g. Omen Gaming Hub using WMI at the same time), retry on next call
                    Logger.WriteLine($"HP WMI 0x{commandType:X2}: {ex.Message}");
                    return null;
                }
            }
        }

        public static int GetTemp()
        {
            var data = Call(TypeTemp, new byte[] { 0x01, 0, 0, 0 }, 4);
            if (data is null || data[0] == 0 || data[0] > 110) return -1;
            return data[0];
        }

        // Fan speeds in 100 RPM units, same as AsusACPI.GetFan
        static int[]? ReadFans()
        {
            var levels = Call(TypeFanLevel, new byte[] { 0, 0, 0, 0 }, 128);
            if (levels is not null) return new[] { (int)levels[0], (int)levels[1] };

            int[] fans = new int[2];
            for (int i = 0; i < fans.Length; i++)
            {
                var data = Call(TypeFanSpeed, new byte[] { (byte)i, 0, 0, 0 }, 4);
                if (data is null) return null;
                fans[i] = ((data[2] << 8) | data[3]) / 100;
            }
            return fans;
        }

        // Maps G-Helper base modes (Balanced / Turbo / Silent) to the Victus S profile + GPU power settings
        public static bool SetPerformanceMode(int mode)
        {
            bool turbo = mode == AsusACPI.PerformanceTurbo;
            bool silent = mode == AsusACPI.PerformanceSilent;

            // Omen Gaming Hub always sends 0xFF as the first byte
            bool ok = Call(TypeThermalProfile, new byte[] { 0xFF, turbo ? ProfilePerformance : ProfileDefault, 0, 0 }, 0) is not null;
            Logger.WriteLine($"HP Thermal Profile = {(turbo ? "Performance" : "Default")} : {(ok ? "OK" : "Failed")}");

            var gpu = Call(TypeGetGpuPower, new byte[] { 0, 0, 0, 0 }, 4);
            if (gpu is not null)
            {
                gpu[0] = (byte)(turbo ? 1 : 0);   // cTGP: extra GPU wattage
                gpu[1] = (byte)(silent ? 0 : 1);  // PPAB: Dynamic Boost CPU -> GPU
                bool gpuOk = Call(TypeSetGpuPower, gpu[..4], 4) is not null;
                Logger.WriteLine($"HP GPU Power = cTGP {gpu[0]} PPAB {gpu[1]} : {(gpuOk ? "OK" : "Failed")}");
            }

            return ok;
        }

        public static bool SetFanLevel(int cpu, int gpu)
        {
            return Call(TypeSetFanLevel, new byte[] { (byte)Math.Clamp(cpu, 0, 255), (byte)Math.Clamp(gpu, 0, 255) }, 0) is not null;
        }

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
            return _fans[(int)device];
        }
    }
}
