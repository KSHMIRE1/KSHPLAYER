using System.Linq;
namespace KSHPLAYER;
internal static class TestMain
{
    private static int Main(string[] a) => a.Length > 0 && a[0] == "--intros" ? Intros.Run(a.Skip(1).ToArray()) : 2;
}
