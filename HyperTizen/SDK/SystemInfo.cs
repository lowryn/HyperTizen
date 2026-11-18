using System;
using Tizen.System;

namespace HyperTizen.SDK
{
    public static class SystemInfo
    {
        public static string PlatformVersion
        {
            get
            {
                try
                {
                    return Information.TryGetValue("http://tizen.org/feature/platform.version", out string version)
                        ? version
                        : null;
                }
                catch (Exception)
                {
                    return null;
                }
            }
        }

        // 0 when the version cannot be read; callers treat that as "old firmware".
        public static int TizenVersionMajor
        {
            get
            {
                string version = PlatformVersion;
                if (string.IsNullOrEmpty(version)) return 0;
                return int.TryParse(version.Split('.')[0], out int major) ? major : 0;
            }
        }

        public static string ModelName
        {
            get
            {
                try
                {
                    return Information.TryGetValue("http://tizen.org/system/model_name", out string name) ? name : null;
                }
                catch (Exception)
                {
                    return null;
                }
            }
        }
    }
}
