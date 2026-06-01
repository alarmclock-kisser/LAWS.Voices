using System;
using System.Collections.Generic;
using System.Linq;
using LAWS.Voices.Multimodal.Audio.Processors;

namespace LAWS.Voices.Forms.Helpers
{
    /// <summary>
    /// Pure, UI-independent helpers for computing authoritative (variable-length) audio
    /// segments around a fingerprint node. Extracted from ResultVisualizerForm so the
    /// segment math can be unit-tested and reused without touching WinForms state.
    /// </summary>
    internal static class FingerprintSegmentMath
    {
        /// <summary>
        /// Computes the authoritative segment for a node inside a full fingerprint list.
        /// Nodes without a track association (TrackId == Guid.Empty) are treated as
        /// self-contained, already variable-length blocks and are NOT merged across the
        /// whole list (that would incorrectly collapse everything into one max-length block).
        /// </summary>
        public static (DateTime start, DateTime end) ExpandAroundIndex(
            List<FingerprintingProcessor.Fingerprint> fps,
            int index,
            int mergeGapMs = 220,
            int maxSegmentMs = 12000)
        {
            if (fps == null || fps.Count == 0)
            {
                return (DateTime.MinValue, DateTime.MinValue);
            }

            if (index < 0) index = 0;
            if (index >= fps.Count) index = fps.Count - 1;
            var target = fps[index];

            if (target.TrackId == Guid.Empty)
            {
                return SelfContained(target, maxSegmentMs);
            }

            var related = fps.Where(f => f.TrackId == target.TrackId)
                .OrderBy(f => f.Timestamp)
                .ToList();

            return ExpandInList(related, target, mergeGapMs, maxSegmentMs);
        }

        /// <summary>
        /// Treats a single fingerprint as a self-contained segment based on its own
        /// timestamp and duration, capped at <paramref name="maxSegmentMs"/>.
        /// </summary>
        public static (DateTime start, DateTime end) SelfContained(
            FingerprintingProcessor.Fingerprint target,
            int maxSegmentMs = 12000)
        {
            DateTime start = target.Timestamp;
            DateTime end = start.AddMilliseconds(Math.Max(20, target.DurationMs));
            if ((end - start).TotalMilliseconds > maxSegmentMs)
            {
                end = start.AddMilliseconds(maxSegmentMs);
            }

            return (start, end);
        }

        /// <summary>
        /// Expands a segment around <paramref name="target"/> within a pre-grouped,
        /// time-ordered list of fingerprints that all share the same TrackId.
        /// </summary>
        public static (DateTime start, DateTime end) ExpandInList(
            List<FingerprintingProcessor.Fingerprint> related,
            FingerprintingProcessor.Fingerprint target,
            int mergeGapMs = 220,
            int maxSegmentMs = 12000)
        {
            if (related == null || related.Count == 0)
            {
                return SelfContained(target, maxSegmentMs);
            }

            int i = related.FindIndex(f => f.Id == target.Id && f.Timestamp == target.Timestamp);
            if (i < 0) i = related.FindIndex(f => f.Timestamp == target.Timestamp);
            if (i < 0) i = 0;

            DateTime start = related[i].Timestamp;
            DateTime end = start.AddMilliseconds(Math.Max(20, related[i].DurationMs));

            for (int L = i - 1; L >= 0; L--)
            {
                var prev = related[L];
                var prevEnd = prev.Timestamp.AddMilliseconds(Math.Max(20, prev.DurationMs));
                if ((start - prevEnd).TotalMilliseconds <= mergeGapMs)
                {
                    start = prev.Timestamp;
                }
                else
                {
                    break;
                }
            }

            for (int R = i + 1; R < related.Count; R++)
            {
                var next = related[R];
                if ((next.Timestamp - end).TotalMilliseconds <= mergeGapMs)
                {
                    end = next.Timestamp.AddMilliseconds(Math.Max(20, next.DurationMs));
                    if ((end - start).TotalMilliseconds >= maxSegmentMs)
                    {
                        end = start.AddMilliseconds(maxSegmentMs);
                        break;
                    }
                }
                else
                {
                    break;
                }
            }

            if ((end - start).TotalMilliseconds > maxSegmentMs)
            {
                end = start.AddMilliseconds(maxSegmentMs);
            }

            return (start, end);
        }

        /// <summary>
        /// Converts a [start,end] timestamp segment into an interleaved sample range
        /// [startSample, endSample) relative to <paramref name="audioCreatedAt"/>, applying
        /// a small symmetric padding (in ms) and clamping to the available data length.
        /// Returns false if the resulting range is empty.
        /// </summary>
        public static bool TryGetSampleRange(
            DateTime segStart,
            DateTime segEnd,
            DateTime audioCreatedAt,
            int sampleRate,
            int channels,
            long dataLength,
            double paddingMs,
            out long startSample,
            out long endSample)
        {
            DateTime sdt = segStart.AddMilliseconds(-paddingMs);
            DateTime edt = segEnd.AddMilliseconds(paddingMs);

            double startFrame = (sdt - audioCreatedAt).TotalSeconds * sampleRate;
            double endFrame = (edt - audioCreatedAt).TotalSeconds * sampleRate;

            startSample = (long) Math.Max(0, Math.Round(startFrame)) * channels;
            endSample = (long) Math.Min(dataLength, Math.Max(startSample + 1, (long) Math.Round(endFrame) * channels));

            return endSample > startSample;
        }
    }
}
