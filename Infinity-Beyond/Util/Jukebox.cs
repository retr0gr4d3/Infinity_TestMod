using MelonLoader;

namespace Infinity_TestMod.Util
{
    /// <summary>
    /// Loads + plays an arbitrary soundtrack by ID. Mirrors what
    /// Area.loadBGM does for the current map, but driven on demand:
    ///   1. If BGMusicManager already has the track cached, just PlayMusic.
    ///   2. Otherwise spin up a SoundtrackLoader → fetches
    ///      data/getsoundtracks?ids=&lt;id&gt; → downloads the bundle →
    ///      OnAssetLoaded fires with the AudioClip → register + play.
    /// </summary>
    public static class Jukebox
    {
        public static void Play(int id)
        {
            if (id <= 0)
            {
                MelonLogger.Warning($"[Jukebox] invalid id {id}");
                return;
            }
            BGMusicManager bgm = BGMusicManager.Instance;
            if (bgm == null)
            {
                MelonLogger.Warning("[Jukebox] no BGMusicManager (no map loaded?)");
                return;
            }

            // Fast path — already loaded this track at some point.
            if (bgm.CustomBGMTracks != null && bgm.CustomBGMTracks.ContainsKey(id))
            {
                bgm.PlayMusic(id, delay: false);
                MelonLogger.Msg($"[Jukebox] playing cached track {id} ({bgm.GetMusicNameFromID(id)})");
                return;
            }

            try
            {
                SoundtrackLoader ldr = new();
                ldr.OnAssetLoaded += (prefabName, clip) =>
                {
                    try
                    {
                        BGMusicManager.AddTrack(id, 0, KeepAlive: false, clip, prefabName, id, ldr);
                        BGMusicManager.Instance?.PlayMusic(id, delay: false);
                        MelonLogger.Msg($"[Jukebox] loaded + playing track {id} ({prefabName})");
                    }
                    catch (System.Exception ex)
                    {
                        MelonLogger.Error($"[Jukebox] add/play failed: {ex.Message}");
                    }
                };
                ldr.LoadFailed += err => MelonLogger.Warning($"[Jukebox] load failed: {err}");
                ldr.LoadSountrackByID(id);
                MelonLogger.Msg($"[Jukebox] fetching track {id}…");
            }
            catch (System.Exception ex)
            {
                MelonLogger.Error($"[Jukebox] {ex.Message}");
            }
        }

        public static void Stop()
        {
            try { BGMusicManager.Instance?.StopMusic(); }
            catch (System.Exception ex) { MelonLogger.Error($"[Jukebox] stop: {ex.Message}"); }
        }

        public static void RestoreAreaBGM()
        {
            try { BGMusicManager.Instance?.RestoreAreaBGM(); }
            catch (System.Exception ex) { MelonLogger.Error($"[Jukebox] restore: {ex.Message}"); }
        }
    }
}
