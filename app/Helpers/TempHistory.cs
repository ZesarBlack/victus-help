namespace GHelper.Helpers
{
    public struct TempSample
    {
        public long Time;
        public float? Cpu;
        public float? Gpu;
    }

    // Session min/max and a rolling window of CPU/GPU temperatures, fed by the existing sensor reads
    public static class TempHistory
    {
        public const int WindowSeconds = 600;
        const int SampleInterval = 2000;

        static readonly object _lock = new();
        static readonly LinkedList<TempSample> _samples = new();
        static long _lastSample;

        public static float? CpuMin, CpuMax, GpuMin, GpuMax;
        static double _cpuSum, _gpuSum;
        static int _cpuCount, _gpuCount;

        public static float? CpuAvg => _cpuCount > 0 ? (float)(_cpuSum / _cpuCount) : null;
        public static float? GpuAvg => _gpuCount > 0 ? (float)(_gpuSum / _gpuCount) : null;

        static float? Valid(float? temp) => (temp is not null && temp > 0 && temp < 125) ? temp : null;

        public static void Record(float? cpu, float? gpu)
        {
            long now = DateTimeOffset.Now.ToUnixTimeMilliseconds();

            cpu = Valid(cpu);
            gpu = Valid(gpu);
            if (cpu is null && gpu is null) return;

            lock (_lock)
            {
                if (now - _lastSample < SampleInterval) return;
                _lastSample = now;

                if (cpu is not null)
                {
                    CpuMin = CpuMin is null ? cpu : Math.Min((float)CpuMin, (float)cpu);
                    CpuMax = CpuMax is null ? cpu : Math.Max((float)CpuMax, (float)cpu);
                    _cpuSum += (float)cpu;
                    _cpuCount++;
                }

                if (gpu is not null)
                {
                    GpuMin = GpuMin is null ? gpu : Math.Min((float)GpuMin, (float)gpu);
                    GpuMax = GpuMax is null ? gpu : Math.Max((float)GpuMax, (float)gpu);
                    _gpuSum += (float)gpu;
                    _gpuCount++;
                }

                _samples.AddLast(new TempSample { Time = now, Cpu = cpu, Gpu = gpu });
                while (_samples.First is not null && now - _samples.First.Value.Time > WindowSeconds * 1000)
                    _samples.RemoveFirst();
            }
        }

        public static TempSample[] GetSamples()
        {
            lock (_lock) return _samples.ToArray();
        }

        public static void Reset()
        {
            lock (_lock)
            {
                _samples.Clear();
                _lastSample = 0;
                CpuMin = CpuMax = GpuMin = GpuMax = null;
                _cpuSum = _gpuSum = 0;
                _cpuCount = _gpuCount = 0;
            }
        }
    }
}
