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

        static System.Timers.Timer? _timer;
        static byte[] _cpuCurve = [];
        static byte[] _gpuCurve = [];

        static int _lastCpu = -1, _lastGpu = -1;
        static long _lastSent;

        public static bool IsRunning => _timer is not null;

        public static void Start(byte[] cpuCurve, byte[] gpuCurve)
        {
            if (AsusACPI.IsInvalidCurve(cpuCurve) || AsusACPI.IsInvalidCurve(gpuCurve))
            {
                Logger.WriteLine("HP Fan Curve: invalid curve");
                Stop();
                return;
            }

            _cpuCurve = cpuCurve;
            _gpuCurve = gpuCurve;
            _lastCpu = _lastGpu = -1;

            if (_timer is null)
            {
                _timer = new System.Timers.Timer(Interval) { AutoReset = false };
                _timer.Elapsed += (_, _) => { Apply(); _timer?.Start(); };
            }

            Logger.WriteLine($"HP Fan Curve: CPU {BitConverter.ToString(cpuCurve)} GPU {BitConverter.ToString(gpuCurve)}");
            Task.Run(Apply);
            _timer.Start();
        }

        public static void Stop()
        {
            if (_timer is null) return;

            _timer.Dispose();
            _timer = null;

            // Re-applying the thermal profile hands fan control back to the BIOS
            HpWmi.SetPerformanceMode(Modes.GetCurrentBase());
            Logger.WriteLine("HP Fan Curve: off");
        }

        static void Apply()
        {
            if (_timer is null) return;

            float? cpuTemp = HardwareControl.GetCPUTemp();
            float? gpuTemp = HardwareControl.GetGPUTemp();

            if (cpuTemp is null || cpuTemp <= 0) return;
            if (gpuTemp is null || gpuTemp <= 0) gpuTemp = cpuTemp; // dGPU asleep

            int cpu = ToLevel(AsusFan.CPU, Interpolate(_cpuCurve, (float)cpuTemp));
            int gpu = ToLevel(AsusFan.GPU, Interpolate(_gpuCurve, (float)gpuTemp));

            long now = Environment.TickCount64;
            if (cpu == _lastCpu && gpu == _lastGpu && now - _lastSent < KeepAlive) return;

            if (HpWmi.SetFanLevel(cpu, gpu))
            {
                if (cpu != _lastCpu || gpu != _lastGpu)
                    Logger.WriteLine($"HP Fan Level: CPU {cpuTemp}°C -> {cpu * 100} RPM, GPU {gpuTemp}°C -> {gpu * 100} RPM");

                _lastCpu = cpu;
                _lastGpu = gpu;
                _lastSent = now;
            }
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
