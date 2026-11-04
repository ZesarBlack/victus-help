using GHelper.Fan;
using GHelper.Helpers;
using GHelper.Mode;

namespace GHelper.Hp
{
    // HP BIOS has no fan curve support, so the curve is applied in software:
    // read temps, interpolate the G-Helper curve and force fan levels
    public static class HpFanControl
    {
        const int Interval = 2000;
        const int KeepAlive = 60000; // BIOS drops forced levels after ~2 min

        public const int PresetOff = -1;
        public const int PresetCount = 3; // Silent, Standard, Gaming
        public const int MinRpm = 1000;
        public const int MaxRpm = 6500;
        public const int RpmStep = 100;

        static readonly int[] DefaultRpm = { 2000, 3500, 5000 };

        // Fixed speeds can be too low for the load: go to max speed when hot, back below HotOff
        const int HotOn = 90;
        const int HotOff = 80;

        static readonly object _lock = new();
        static System.Timers.Timer? _timer;
        static byte[] _cpuCurve = [];
        static byte[] _gpuCurve = [];
        static bool _usePreset;

        static int _lastCpu = -1, _lastGpu = -1;
        static long _lastSent;

        public static bool IsRunning => _timer is not null;
        public static bool IsProtecting { get; private set; }

        public static int GetPreset()
        {
            int preset = AppConfig.Get("hp_fan_preset", PresetOff);
            return preset >= 0 && preset < PresetCount ? preset : PresetOff;
        }

        public static int GetPresetRpm(int preset, AsusFan device)
        {
            return Math.Clamp(AppConfig.Get($"hp_fan_rpm_{preset}_{(int)device}", DefaultRpm[preset]), MinRpm, MaxRpm);
        }

        public static void SetPresetRpm(int preset, AsusFan device, int rpm)
        {
            AppConfig.Set($"hp_fan_rpm_{preset}_{(int)device}", Math.Clamp(rpm, MinRpm, MaxRpm));
        }

        // Activates a preset and sends it right away, false if the BIOS doesn't accept it
        public static bool SetPreset(int preset)
        {
            AppConfig.Set("hp_fan_preset", preset);
            if (preset == PresetOff) return true;

            StartPreset();
            if (ApplyNow()) return true;

            AppConfig.Set("hp_fan_preset", PresetOff);
            return false;
        }

        public static void StartPreset()
        {
            lock (_lock)
            {
                _usePreset = true;
                StartTimer();
            }

            Logger.WriteLine($"HP Fan Preset: {GetPreset()}");
            Task.Run(ApplyNow);
        }

        public static void Start(byte[] cpuCurve, byte[] gpuCurve)
        {
            if (AsusACPI.IsInvalidCurve(cpuCurve) || AsusACPI.IsInvalidCurve(gpuCurve))
            {
                Logger.WriteLine("HP Fan Curve: invalid curve");
                Stop();
                return;
            }

            lock (_lock)
            {
                _cpuCurve = cpuCurve;
                _gpuCurve = gpuCurve;
                _usePreset = false;
                StartTimer();
            }

            Logger.WriteLine($"HP Fan Curve: CPU {BitConverter.ToString(cpuCurve)} GPU {BitConverter.ToString(gpuCurve)}");
            Task.Run(ApplyNow);
        }

        static void StartTimer()
        {
            _lastCpu = _lastGpu = -1;
            IsProtecting = false;

            if (_timer is null)
            {
                _timer = new System.Timers.Timer(Interval) { AutoReset = false };
                _timer.Elapsed += (_, _) => { ApplyNow(); _timer?.Start(); };
            }

            _timer.Start();
        }

        public static void Stop()
        {
            lock (_lock)
            {
                if (_timer is null) return;

                _timer.Dispose();
                _timer = null;
                IsProtecting = false;

                // Re-applying the thermal profile hands fan control back to the BIOS
                HpWmi.SetPerformanceMode(Modes.GetCurrentBase());
                Logger.WriteLine("HP Fan Control: off");
            }
        }

        static bool ApplyNow()
        {
            lock (_lock)
            {
                if (_timer is null) return false;
                return _usePreset ? ApplyPreset() : ApplyCurve();
            }
        }

        static bool ApplyPreset()
        {
            int preset = GetPreset();
            if (preset == PresetOff) return false;

            float? cpuTemp = HardwareControl.GetCPUTemp();
            float? gpuTemp = HardwareControl.GetGPUTemp();

            bool noTemp = cpuTemp is null || cpuTemp <= 0;
            float hottest = Math.Max(cpuTemp ?? 0, gpuTemp ?? 0);

            bool protect = noTemp || hottest >= HotOn || (IsProtecting && hottest >= HotOff);
            if (protect != IsProtecting)
                Logger.WriteLine($"HP Fan Protection: {(protect ? "on" : "off")}, CPU {cpuTemp}°C GPU {gpuTemp}°C");
            IsProtecting = protect;

            int cpu = (int)Math.Round(GetPresetRpm(preset, AsusFan.CPU) / 100.0);
            int gpu = (int)Math.Round(GetPresetRpm(preset, AsusFan.GPU) / 100.0);

            if (protect)
            {
                cpu = Math.Max(cpu, FanSensorControl.GetFanMax(AsusFan.CPU));
                gpu = Math.Max(gpu, FanSensorControl.GetFanMax(AsusFan.GPU));
            }

            return SendLevels(cpu, gpu, cpuTemp, gpuTemp);
        }

        static bool ApplyCurve()
        {
            float? cpuTemp = HardwareControl.GetCPUTemp();
            float? gpuTemp = HardwareControl.GetGPUTemp();

            if (cpuTemp is null || cpuTemp <= 0) return false;
            if (gpuTemp is null || gpuTemp <= 0) gpuTemp = cpuTemp; // dGPU asleep

            int cpu = ToLevel(AsusFan.CPU, Interpolate(_cpuCurve, (float)cpuTemp));
            int gpu = ToLevel(AsusFan.GPU, Interpolate(_gpuCurve, (float)gpuTemp));

            return SendLevels(cpu, gpu, cpuTemp, gpuTemp);
        }

        static bool SendLevels(int cpu, int gpu, float? cpuTemp, float? gpuTemp)
        {
            long now = Environment.TickCount64;
            if (cpu == _lastCpu && gpu == _lastGpu && now - _lastSent < KeepAlive) return true;

            if (!HpWmi.SetFanLevel(cpu, gpu)) return false;

            if (cpu != _lastCpu || gpu != _lastGpu)
                Logger.WriteLine($"HP Fan Level: CPU {cpuTemp}°C -> {cpu * 100} RPM, GPU {gpuTemp}°C -> {gpu * 100} RPM");

            _lastCpu = cpu;
            _lastGpu = gpu;
            _lastSent = now;
            return true;
        }

        // Curve: 8 temperatures followed by 8 fan percentages
        static float Interpolate(byte[] curve, float temp)
        {
            if (temp <= curve[0]) return curve[8];

            for (int i = 1; i < 8; i++)
            {
                if (temp <= curve[i])
                {
                    float t0 = curve[i - 1], t1 = curve[i];
                    float p0 = curve[i + 7], p1 = curve[i + 8];
                    return t1 == t0 ? p1 : p0 + (p1 - p0) * (temp - t0) / (t1 - t0);
                }
            }

            return curve[15];
        }

        static int ToLevel(AsusFan device, float percent)
        {
            return (int)Math.Round(Math.Clamp(percent, 0, 100) / 100 * FanSensorControl.GetFanMax(device));
        }
    }
}
