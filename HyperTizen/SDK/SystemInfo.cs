using Tizen.System;

namespace HyperTizen.SDK
{
    public static class SystemInfo
    {
        public static int TizenVersionMajor
        {
            get
            {
                Information.TryGetValue("http://tizen.org/feature/platform.version", out string version);
                return int.Parse(version.Split('.')[0]);
            }
        }
    }
}
