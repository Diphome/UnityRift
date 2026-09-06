using System;

namespace UnityRift
{
    public static class Progress
    {
        private static readonly int InstanceCount = 2;
        private static readonly IProgress<int>[] Instances;
        private static readonly int[] PreValues;

        static Progress()
        {
            Instances = new IProgress<int>[InstanceCount];
            for (var i = 0; i < InstanceCount; i++)
            {
                Instances[i] = new Progress<int>();
            }

            PreValues = new int[InstanceCount];
        }

        public static int MaxCount => InstanceCount;

        public static IProgress<int> Default //alias
        {
            get => Instances[0];
            set => SetInstance(0, value);
        }

        public static void Reset(int index = 0)
        {
            PreValues[index] = 0;
            Instances[index].Report(0);
        }

        public static void Report(int current, int total, int index = 0)
        {
            var value = (int)(current * 100f / total);
            _Report(value, index);
        }

        private static void _Report(int value, int index)
        {
            // Called from worker threads during loading; only publish increases.
            var previous = System.Threading.Volatile.Read(ref PreValues[index]);
            while (value > previous)
            {
                var seen = System.Threading.Interlocked.CompareExchange(ref PreValues[index], value, previous);
                if (seen == previous)
                {
                    Instances[index].Report(value);
                    return;
                }
                previous = seen;
            }
        }

        public static void SetInstance(int index, IProgress<int> progress)
        {
            if (progress == null)
                throw new ArgumentNullException(nameof(progress));
            if (index < 0 || index >= MaxCount)
                throw new ArgumentOutOfRangeException(nameof(index));

            Instances[index] = progress;
        }
    }
}
