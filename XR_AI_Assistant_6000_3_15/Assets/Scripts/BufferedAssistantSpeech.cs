using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Meta.Voice.Audio;
using Meta.WitAi.TTS;
using Meta.WitAi.TTS.Data;
using Meta.WitAi.TTS.Utilities;
using UnityEngine;

/// <summary>
/// One answer's TTS lifetime. Download the entire answer before allowing the
/// existing SDK speaker to play it, so a slow hotspot cannot starve its looped
/// streaming buffer halfway through a sentence. No audio components are added.
/// </summary>
internal sealed class BufferedAssistantSpeech
{
    private readonly TTSSpeaker speaker;
    private readonly List<TTSClipData> clips = new List<TTSClipData>();
    private TTSService service;
    private bool stopped;

    internal BufferedAssistantSpeech(TTSSpeaker speaker)
    {
        this.speaker = speaker;
    }

    internal async Task SpeakAsync(string text, Action checkActive)
    {
        Action checkSession = () =>
        {
            checkActive();
            if (stopped || speaker == null || !speaker.isActiveAndEnabled)
                throw new OperationCanceledException();
        };
        try
        {
            checkSession();
            if (string.IsNullOrWhiteSpace(text))
                return;

            speaker.Stop();
            service = speaker.TTSService;
            if (service == null || speaker.VoiceSettings == null)
                throw new InvalidOperationException("Speech service or voice is unavailable.");

            // The scene's raw stream has a 15-second fixed capacity. Keep input
            // chunks short before applying the speaker's usual text processors,
            // and use these exact chunks again for queued SDK playback.
            string[] chunks = SplitSpeechInput(text);
            var phrases = new List<string>();
            foreach (string chunk in chunks) phrases.AddRange(speaker.GetFinalText(chunk));
            float duration = 0f;
            float loadDeadline = Time.realtimeSinceStartup + 30f;
            foreach (string phrase in phrases)
            {
                checkSession();
                if (string.IsNullOrWhiteSpace(phrase)) continue;
                TTSClipData clip = service.GetClipData(phrase, speaker.VoiceSettings, null);
                clips.Add(clip);
                Task<string> loading = service.LoadAsync(clip);
                await WaitForTask(loading, loadDeadline, checkSession);
                string error = await loading;
                if (!string.IsNullOrEmpty(error) || clip.loadState != TTSClipLoadState.Loaded ||
                    clip.clipStream == null || !clip.clipStream.IsComplete ||
                    clip.clipStream.AddedSamples <= 0)
                    throw new InvalidOperationException("Speech audio did not finish loading.");
                // Voice 85's RawAudioClipStream silently drops excess samples,
                // then TTSWit sets ExpectedSamples to AddedSamples. IsComplete
                // alone can therefore accept a truncated clip. Reject even an
                // exact-capacity clip because it is indistinguishable from loss.
                if (clip.clipStream is RawAudioClipStream raw &&
                    raw.AddedSamples >= raw.SampleBuffer.Length)
                    throw new InvalidOperationException("Speech audio exceeded its safe buffer capacity.");
                duration += clip.clipStream.Length;
            }

            if (clips.Count == 0) return;
            checkSession();
            // Fail closed if a cache policy evicted a phrase. Otherwise queued playback
            // would silently re-download it and start partial streaming again.
            foreach (TTSClipData clip in clips)
            {
                if (!ReferenceEquals(service.GetRuntimeCachedClip(clip.clipID), clip))
                    throw new InvalidOperationException("Completed speech audio left the cache.");
            }

            speaker.Resume();
            Task playback = speaker.SpeakQueuedTask(chunks);
            float pitch = speaker.AudioSource != null ? Mathf.Abs(speaker.AudioSource.pitch) : 1f;
            float playbackDeadline = Time.realtimeSinceStartup + duration / Mathf.Max(0.1f, pitch) + 5f;
            float progressDeadline = Time.realtimeSinceStartup + 5f;
            TTSClipData lastClip = null;
            int lastSample = -1;
            try
            {
                while (!playback.IsCompleted)
                {
                    checkSession();
                    TTSClipData currentClip = speaker.SpeakingClip;
                    int sample = speaker.ElapsedSamples;
                    if (!ReferenceEquals(currentClip, lastClip) || sample > lastSample)
                    {
                        lastClip = currentClip;
                        lastSample = sample;
                        progressDeadline = Time.realtimeSinceStartup + 5f;
                    }
                    if (Time.realtimeSinceStartup >= playbackDeadline ||
                        Time.realtimeSinceStartup >= progressDeadline)
                        throw new TimeoutException("Speech playback stopped making progress.");
                    await Task.Yield();
                }
                checkSession();
                await playback;
            }
            finally
            {
                ObserveIfPending(playback);
            }
        }
        finally
        {
            Stop();
        }
    }

    internal static string[] SplitSpeechInput(string text)
    {
        // Bound both long words/URLs and short-word lists. A character bound is
        // not a duration guarantee; the completed raw-buffer check remains the
        // final guard for slow voices, abbreviations, numbers, and other scripts.
        const int maxCharacters = 96;
        const int maxWords = 12;
        var chunks = new List<string>();
        int start = 0;
        while (start < text.Length)
        {
            int end = Math.Min(start + maxCharacters, text.Length);
            int words = 0;
            bool inWord = false;
            int lastBreak = -1;
            for (int i = start; i < end; i++)
            {
                if (char.IsWhiteSpace(text[i]))
                {
                    lastBreak = i + 1;
                    inWord = false;
                }
                else if (!inWord)
                {
                    if (++words > maxWords) { end = i; break; }
                    inWord = true;
                }
            }
            if (end < text.Length && lastBreak > start)
                end = lastBreak;
            // Avoid cutting a supplementary Unicode character in half when a
            // long unbroken word must be split. Every non-whitespace char stays.
            if (end < text.Length && end > start && char.IsHighSurrogate(text[end - 1]) && char.IsLowSurrogate(text[end]))
                end--;
            string chunk = text.Substring(start, end - start).Trim();
            if (chunk.Length > 0) chunks.Add(chunk);
            start = end;
        }
        return chunks.ToArray();
    }

    private static async Task WaitForTask(Task task, float deadline, Action checkActive)
    {
        try
        {
            while (!task.IsCompleted)
            {
                checkActive();
                if (Time.realtimeSinceStartup >= deadline)
                    throw new TimeoutException("Speech download timed out.");
                await Task.Yield();
            }
            checkActive();
            await task;
        }
        finally
        {
            ObserveIfPending(task);
        }
    }

    private static void ObserveIfPending(Task task)
    {
        // The SDK owns its transport and cancellation. Observe a late failure
        // after our timeout without letting it resume UI or speech processing.
        if (task.IsFaulted)
            _ = task.Exception;
        else if (!task.IsCompleted)
            _ = task.ContinueWith(t => { _ = t.Exception; },
                TaskContinuationOptions.OnlyOnFaulted);
    }

    internal void Stop()
    {
        // Cancellation may call this before an older await reaches its finally.
        // Do not let that stale finally stop the next answer's speaker.
        if (stopped) return;
        stopped = true;
        if (speaker != null) speaker.Stop();
        if (service != null)
        {
            foreach (TTSClipData clip in clips)
            {
                if (clip != null && ReferenceEquals(service.GetRuntimeCachedClip(clip.clipID), clip))
                    service.Unload(clip);
            }
        }
        clips.Clear();
    }
}
