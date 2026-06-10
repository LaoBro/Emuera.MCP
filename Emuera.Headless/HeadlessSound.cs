#if HEADLESS
namespace MinorShift.Emuera.Runtime.Utils;

internal class Sound
{
    public void play(string filename, int repeat = 1) { }
    public void stop() { }
    public void close() { }
    public void setVolume(int volume) { }
    public bool isPlaying() => false;
}
#endif
