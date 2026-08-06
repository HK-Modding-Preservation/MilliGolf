using ConnectionSettingsRando;
using MilliGolf.Rando.Manager;

namespace MilliGolf.Rando.Interop {
    internal class CSRInterop {
        public static void Hook() {
            CSR.Register(
                MilliGolf.Instance.GetName(),
                () => GolfManager.GlobalSettings,
                s => SettingsRandomizer.CopyTo(s, GolfManager.GlobalSettings)
            );
        }
    }
}
