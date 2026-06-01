using System;
using System.Collections.Generic;
using System.Linq;
using MelonLoader;
using UnityEngine;
using Infinity_TestMod.Patches;

namespace Infinity_TestMod.Util
{
    /// <summary>
    /// End-to-end quest automation. Drives one quest ID repeatedly through
    /// accept → hunt (kill/collect via target+autoskills) → turn-in, halting
    /// on any mismatch and surfacing the reason.
    ///
    /// Reads all progress from live in-process state — no packet replay:
    ///   - Quest defs:        Quest.Get(qid).Turnins[]
    ///   - Current progress:  Entity.mainPlayer.Quests.IsObjectiveComplete(qoid)
    ///   - Target setting:    Entity.mainPlayer.target = monster
    ///   - Combat:            piggybacks on TestMod.autoskillsActive
    ///   - Mob enumeration:   Area.currentArea.GetMonstersInFrame()
    ///   - Requests:          RequestQuestAccept / RequestTryQuestComplete
    ///
    /// Designed to be ticked from MelonMod.OnUpdate so it lives on the main
    /// Unity thread (no marshalling needed for any of the game-side calls).
    /// </summary>
    public class QuestRunner
    {
        public enum RunState
        {
            Idle,
            Accepting,
            AwaitingAccepted,
            Traveling,
            Hunting,
            TurningIn,
            AwaitingComplete,
            Cooldown,
            Done,
            Failed
        }

        // --- config ---
        public int QuestID { get; private set; }
        public int Iterations { get; private set; }
        // Optional in-zone hop before hunting. If TargetFrame is non-empty
        // and the player isn't already in that frame, the runner sends a
        // moveToCell(Frame, Pad) and waits for arrival before proceeding.
        // For cross-zone travel (different area), use the Sender's tfer
        // manually before pressing Start — we don't auto-area-change yet.
        public string TargetFrame { get; private set; } = "";
        public string TargetPad { get; private set; } = "Spawn";

        // Chain mode: when ChainEntries is non-null, the runner walks it
        // sequentially. Each entry rebinds QuestID / TargetFrame / TargetPad
        // / Iterations and re-enters Accepting. ChainIndex tracks position;
        // ChainName is just for status display.
        public List<QuestChains.Entry> ChainEntries { get; private set; }
        public int ChainIndex { get; private set; }
        public string ChainName { get; private set; } = "";

        // Per-state timeouts. Halt-on-mismatch is the QA value prop.
        public float AcceptTimeout = 5f;
        public float TravelTimeout = 6f;
        public float HuntTimeoutNoProgress = 90f;
        public float CompleteTimeout = 8f;
        // Brief delay between consecutive turn-ins / iterations so we don't
        // trip the server's per-action spam cooldown (observed: tryQuestComplete
        // back-to-back returns rNotify "Spam Detected").
        public float InterIterCooldown = 1.5f;

        // --- state ---
        public RunState State { get; private set; } = RunState.Idle;
        public int CurrentIteration { get; private set; }
        public string LastError { get; private set; } = "";
        public string StatusLine { get; private set; } = "idle";

        float _stateEnteredAt;
        float _lastProgressAt;
        int _lastProgressSum;
        // Save autoskills state so we don't fight the user's manual toggle.
        bool _autoskillsWasOn;

        // Logs are surfaced via this callback to the GUI's event list.
        public Action<string> OnLog;

        // --- public API ---

        public bool IsRunning =>
            State != RunState.Idle && State != RunState.Done && State != RunState.Failed;

        public void Start(int questId, int iterations, string targetFrame = "", string targetPad = "Spawn")
        {
            if (IsRunning) return;
            ChainEntries = null;
            ChainName = "";
            ChainIndex = 0;
            BindEntry(questId, iterations, targetFrame, targetPad);
            _autoskillsWasOn = TestMod.autoskillsActive;
            string travelNote = string.IsNullOrEmpty(TargetFrame) ? "" : $", hop to {TargetFrame}/{TargetPad}";
            Log($"[start] quest {questId} × {iterations}{travelNote}");
            EnterState(RunState.Accepting);
        }

        public void StartChain(string chainName, List<QuestChains.Entry> entries)
        {
            if (IsRunning) return;
            if (entries == null || entries.Count == 0)
            {
                Fail("chain is empty");
                return;
            }
            ChainEntries = entries;
            ChainName = chainName ?? "";
            ChainIndex = 0;
            var first = entries[0];
            BindEntry(first.qid, first.iters, first.frame, first.pad);
            _autoskillsWasOn = TestMod.autoskillsActive;
            Log($"[start] chain '{chainName}' ({entries.Count} entries) — first: {first}");
            EnterState(RunState.Accepting);
        }

        void BindEntry(int qid, int iters, string frame, string pad)
        {
            QuestID = qid;
            Iterations = Math.Max(1, iters);
            TargetFrame = frame ?? "";
            TargetPad = string.IsNullOrEmpty(pad) ? "Spawn" : pad;
            CurrentIteration = 0;
            LastError = "";
        }

        public void Stop()
        {
            if (!IsRunning) return;
            Log("[stop] user requested");
            StopAutoskills();
            EnterState(RunState.Idle);
        }

        // --- tick ---

        public void Tick()
        {
            try
            {
                switch (State)
                {
                    case RunState.Accepting:        TickAccept(); break;
                    case RunState.AwaitingAccepted: TickAwaitAccepted(); break;
                    case RunState.Traveling:        TickTravel(); break;
                    case RunState.Hunting:          TickHunt(); break;
                    case RunState.TurningIn:        TickTurnIn(); break;
                    case RunState.AwaitingComplete: TickAwaitComplete(); break;
                    case RunState.Cooldown:         TickCooldown(); break;
                }
            }
            catch (Exception ex)
            {
                Fail($"unhandled exception in state {State}: {ex.Message}");
            }
        }

        // --- state handlers ---

        void TickAccept()
        {
            if (Entity.mainPlayer == null)
            {
                StatusLine = "waiting for mainPlayer";
                return;
            }

            // Skip the send if we're already on this quest — server treats it
            // as a no-op anyway but avoids a wasted packet.
            if (IsQuestAccepted(QuestID))
            {
                Log($"  iter {CurrentIteration + 1}: already accepted, skipping send");
                EnterState(RunState.Hunting);
                return;
            }

            try
            {
                AEC.Instance.sendRequest(new RequestQuestAccept(QuestID));
            }
            catch (Exception ex)
            {
                Fail($"acceptQuest send failed: {ex.Message}");
                return;
            }
            EnterState(RunState.AwaitingAccepted);
        }

        void TickAwaitAccepted()
        {
            if (IsQuestAccepted(QuestID))
            {
                CurrentIteration++;
                Log($"  iter {CurrentIteration}/{Iterations}: accepted");
                // Branch to Traveling if the user set a TargetFrame and we're
                // not already there; otherwise go straight to Hunting.
                if (NeedsCellHop())
                    EnterState(RunState.Traveling);
                else
                    EnterState(RunState.Hunting);
                return;
            }
            if (StateAge() > AcceptTimeout)
            {
                Fail($"quest {QuestID} not in accepted list after {AcceptTimeout:0.0}s — likely a prereq or level gate");
            }
            else
            {
                StatusLine = $"waiting for accept… ({StateAge():0.0}s)";
            }
        }

        void TickCooldown()
        {
            float remaining = InterIterCooldown - StateAge();
            if (remaining > 0f)
            {
                StatusLine = $"cooldown {remaining:0.0}s (avoid spam detect)";
                return;
            }
            // Decide what's next.
            if (CurrentIteration < Iterations)
            {
                EnterState(RunState.Accepting);
                return;
            }
            if (ChainEntries != null && ChainIndex + 1 < ChainEntries.Count)
            {
                ChainIndex++;
                var next = ChainEntries[ChainIndex];
                BindEntry(next.qid, next.iters, next.frame, next.pad);
                Log($"[chain] {ChainIndex + 1}/{ChainEntries.Count}: {next}");
                EnterState(RunState.Accepting);
                return;
            }
            Log(ChainEntries != null
                ? $"[done] chain '{ChainName}' complete"
                : "[done] all iterations complete");
            EnterState(RunState.Done);
        }

        void TickTravel()
        {
            // Already there? Done.
            string here = Entity.mainPlayer?.Frame ?? "";
            if (here == TargetFrame)
            {
                Log($"  arrived at {here}");
                EnterState(RunState.Hunting);
                return;
            }
            // Send the moveToCell once per state-entry. Without a sentinel
            // we'd spam the request every frame, and the server appears to
            // rate-limit / reject duplicates.
            if (!_traveSent)
            {
                try
                {
                    AEC.Instance.sendRequest(new RequestMoveToCell(TargetFrame, TargetPad));
                    _traveSent = true;
                    Log($"  moveToCell({TargetFrame}, {TargetPad})");
                }
                catch (Exception ex)
                {
                    Fail($"moveToCell send failed: {ex.Message}");
                    return;
                }
            }
            if (StateAge() > TravelTimeout)
            {
                Fail($"didn't reach frame '{TargetFrame}' within {TravelTimeout:0.0}s (currently in '{here}') — wrong frame name or movement blocked");
                return;
            }
            StatusLine = $"traveling {here} → {TargetFrame}/{TargetPad}  ({StateAge():0.0}s)";
        }
        bool _traveSent;

        void TickHunt()
        {
            Quest q = Quest.Get(QuestID);
            if (q == null)
            {
                Fail($"no quest def cached for {QuestID} — open the quest UI once to populate Quest.Get()");
                return;
            }

            // Find the first incomplete objective. If none, we're ready to
            // turn in. The quest def's Turnins are the source of truth here —
            // we don't try to interpret them, just check completion per QOID.
            var nextObjective = NextIncompleteObjective(q);
            if (nextObjective == null)
            {
                StopAutoskills();
                Log("  all objectives complete; turning in");
                EnterState(RunState.TurningIn);
                return;
            }

            // Acquire / refresh target on each tick. The setter on
            // Entity.mainPlayer.target no-ops if the value is unchanged AND
            // refuses dead targets, so calling it every frame is cheap.
            Monster tgt = PickBestHostile(nextObjective);
            if (tgt == null)
            {
                StopAutoskills();
                StatusLine = $"no hostile mob in frame for objective '{nextObjective.Name}' ({nextObjective.QOType})";
                // Don't fail yet — frame might be transitioning, mobs respawning.
                CheckHuntTimeout();
                return;
            }

            // Set target only when different — target setter has side effects
            // (event invocation, icon refresh) we don't want every frame.
            if (Entity.mainPlayer.target != tgt)
            {
                Entity.mainPlayer.target = tgt;
            }
            EnsureAutoskillsOn();

            int progressSum = SumObjectiveProgress(q);
            if (progressSum != _lastProgressSum)
            {
                _lastProgressSum = progressSum;
                _lastProgressAt = Time.time;
                StatusLine = $"hunting {tgt.Name} (id={tgt.ID})  progress={progressSum}";
            }
            else
            {
                StatusLine = $"hunting {tgt.Name}  no progress {Time.time - _lastProgressAt:0.0}s";
            }

            CheckHuntTimeout();
        }

        void CheckHuntTimeout()
        {
            if (Time.time - _lastProgressAt > HuntTimeoutNoProgress)
            {
                Fail($"no objective progress in {HuntTimeoutNoProgress:0}s — quest likely needs a different zone, mob type, or interaction we don't handle yet");
            }
        }

        void TickTurnIn()
        {
            try
            {
                AEC.Instance.sendRequest(new RequestTryQuestComplete(QuestID));
            }
            catch (Exception ex)
            {
                Fail($"tryQuestComplete send failed: {ex.Message}");
                return;
            }
            _turnInSentAt = Time.time;
            EnterState(RunState.AwaitingComplete);
        }
        float _turnInSentAt;

        void TickAwaitComplete()
        {
            // Real success signal: ResponseQuestComplete.Execute fired for our
            // quest with Success=true. Captured by Harmony patch into
            // RuntimeEvents (LastCompleteQid / LastCompleteTime / Success).
            // We compare LastCompleteTime against _turnInSentAt so a stale
            // QComp from a previous attempt doesn't false-positive.
            if (RuntimeEvents.LastCompleteQid == QuestID
                && RuntimeEvents.LastCompleteTime > _turnInSentAt
                && RuntimeEvents.LastCompleteSuccess)
            {
                Log($"  iter {CurrentIteration}: turn-in confirmed (QComp success)");
                // Always cool down briefly before the next action — the
                // server's spam-detect window is short but firm. Cooldown
                // state decides what comes next based on remaining work.
                EnterState(RunState.Cooldown);
                return;
            }
            // Watch for an rNotify error after our send. The server uses
            // these for rate-limiting ("Spam Detected") and for quest-side
            // rejection ("You haven't completed…"). Surface them immediately
            // rather than waiting out the full timeout.
            if (RuntimeEvents.LastNotifyTime > _turnInSentAt
                && !string.IsNullOrEmpty(RuntimeEvents.LastNotifyMsg))
            {
                string msg = RuntimeEvents.LastNotifyMsg;
                string lower = msg.ToLowerInvariant();
                if (lower.Contains("spam") || lower.Contains("wait before"))
                {
                    Fail($"server rate-limited turn-in: \"{msg}\" — bump InterIterCooldown");
                    return;
                }
                if (lower.Contains("requirement") || lower.Contains("haven't")
                    || lower.Contains("complete this quest") || lower.Contains("not met"))
                {
                    Fail($"server rejected turn-in: \"{msg}\"");
                    return;
                }
            }
            if (StateAge() > CompleteTimeout)
            {
                Fail($"no QComp success for quest {QuestID} within {CompleteTimeout:0.0}s — last rNotify: \"{RuntimeEvents.LastNotifyMsg}\"");
            }
            else
            {
                StatusLine = $"waiting for QComp… ({StateAge():0.0}s)";
            }
        }

        // --- helpers ---

        bool NeedsCellHop()
        {
            if (string.IsNullOrEmpty(TargetFrame)) return false;
            string here = Entity.mainPlayer?.Frame ?? "";
            return here != TargetFrame;
        }

        bool IsQuestAccepted(int id)
        {
            try
            {
                return Entity.mainPlayer?.Quests?.GetQuest(id) != null;
            }
            catch
            {
                return false;
            }
        }

        QuestTurninItem NextIncompleteObjective(Quest q)
        {
            if (q?.Turnins == null) return null;
            var pq = Entity.mainPlayer?.Quests;
            if (pq == null) return null;
            foreach (var t in q.Turnins)
            {
                if (!pq.IsObjectiveComplete(t.QOID)) return t;
            }
            return null;
        }

        int SumObjectiveProgress(Quest q)
        {
            if (q?.Turnins == null) return 0;
            var pq = Entity.mainPlayer?.Quests;
            if (pq == null) return 0;
            int sum = 0;
            foreach (var t in q.Turnins)
            {
                sum += pq.getQuestObjective(t.QOID)?.Quantity ?? 0;
            }
            return sum;
        }

        Monster PickBestHostile(QuestTurninItem obj)
        {
            if (Area.currentArea == null || Entity.mainPlayer == null) return null;

            // Killcount objectives ref a MonID. For Turnin/item objectives
            // we can't reliably resolve "which mob drops this item" without
            // server-side loot tables we don't have access to here, so we
            // fall back to "any hostile in frame" — works for the common
            // case where the player parked next to the right respawn point.
            int? requiredMonId = null;
            if (obj.QOType == QuestObjectiveType.Killcount && obj.RefArray != null && obj.RefArray.Length > 0
                && int.TryParse(obj.RefArray[0], out int parsed))
            {
                requiredMonId = parsed;
            }

            IEnumerable<Monster> candidates;
            try
            {
                candidates = Area.currentArea.GetMonstersInFrame();
            }
            catch
            {
                return null;
            }
            if (candidates == null) return null;

            var alive = candidates.Where(m =>
                m != null
                && m.currentState != Entity.State.Dead
                && m.reactionType == Entity.ReactionType.Hostile
                && (requiredMonId == null || m.ID == requiredMonId.Value));

            // Nearest by Combat's own range semantics — IsInSight first,
            // then distance. Reusing the game's comparer keeps targeting
            // consistent with what a manual-clicking player would pick.
            var list = alive.ToList();
            if (list.Count == 0) return null;
            list.Sort(new TargetDistanceComparer(Entity.mainPlayer));
            return list[0];
        }

        void EnsureAutoskillsOn()
        {
            if (!TestMod.autoskillsActive) TestMod.autoskillsActive = true;
        }

        void StopAutoskills()
        {
            // Restore user's autoskills toggle to whatever they had before
            // we started. If they had it off, leave it off — they shouldn't
            // discover the bot left their character spinning.
            TestMod.autoskillsActive = _autoskillsWasOn;
        }

        void EnterState(RunState s)
        {
            State = s;
            _stateEnteredAt = Time.time;
            if (s == RunState.Hunting)
            {
                _lastProgressAt = Time.time;
                _lastProgressSum = SumObjectiveProgress(Quest.Get(QuestID));
            }
            if (s == RunState.Traveling)
            {
                _traveSent = false;
            }
        }

        float StateAge() => Time.time - _stateEnteredAt;

        void Fail(string why)
        {
            LastError = why;
            StatusLine = $"FAIL: {why}";
            Log($"[fail] {why}");
            StopAutoskills();
            EnterState(RunState.Failed);
        }

        void Log(string line)
        {
            try { OnLog?.Invoke(line); } catch { }
            MelonLogger.Msg($"[QuestRunner] {line}");
        }
    }
}
