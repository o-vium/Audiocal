using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NAudio.Wave;

namespace Audiocal.Services
{
    public sealed class AudioAnalysisService
    {
        private const int CacheVersion = 1;
        public const int SampleIntervalMilliseconds = 20;

        private static readonly string CacheDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Audiocal",
            "AudioAnalysis");

        public Task<AudioAnalysisData> GetOrCreateAsync(
            string audioPath,
            CancellationToken cancellationToken)
        {
            return Task.Run(() => GetOrCreate(audioPath, cancellationToken), cancellationToken);
        }

        private static AudioAnalysisData GetOrCreate(string audioPath, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fileInfo = new FileInfo(audioPath);
            if (!fileInfo.Exists)
            {
                throw new FileNotFoundException("The audio file was not found.", audioPath);
            }

            long fileLength = fileInfo.Length;
            long lastWriteUtcTicks = fileInfo.LastWriteTimeUtc.Ticks;
            string cachePath = GetCachePath(audioPath);
            if (TryLoadCache(cachePath, fileLength, lastWriteUtcTicks, out AudioAnalysisData? cached) &&
                cached != null)
            {
                return cached;
            }

            AudioAnalysisData analysis = Analyze(audioPath, fileLength, lastWriteUtcTicks, cancellationToken);
            WriteCache(cachePath, analysis, cancellationToken);
            return analysis;
        }

        private static bool TryLoadCache(
            string cachePath,
            long fileLength,
            long lastWriteUtcTicks,
            out AudioAnalysisData? analysis)
        {
            analysis = null;
            if (!File.Exists(cachePath))
            {
                return false;
            }

            try
            {
                using var stream = File.OpenRead(cachePath);
                using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: false);
                if (reader.ReadInt32() != CacheVersion ||
                    reader.ReadInt64() != fileLength ||
                    reader.ReadInt64() != lastWriteUtcTicks)
                {
                    return false;
                }

                int interval = reader.ReadInt32();
                int count = reader.ReadInt32();
                if (interval != SampleIntervalMilliseconds ||
                    count <= 0 ||
                    count > 10_000_000 ||
                    stream.Length - stream.Position != count * sizeof(float))
                {
                    return false;
                }

                var bassLevels = new float[count];
                for (int i = 0; i < count; i++)
                {
                    float level = reader.ReadSingle();
                    if (!float.IsFinite(level) || level < 0 || level > 1)
                    {
                        return false;
                    }

                    bassLevels[i] = level;
                }

                analysis = new AudioAnalysisData(bassLevels, interval);
                return true;
            }
            catch (Exception exception) when (
                exception is IOException or
                UnauthorizedAccessException or
                ArgumentException)
            {
                return false;
            }
        }

        private static AudioAnalysisData Analyze(
            string audioPath,
            long fileLength,
            long lastWriteUtcTicks,
            CancellationToken cancellationToken)
        {
            using var reader = new AudioFileReader(audioPath);
            int channelCount = Math.Max(1, reader.WaveFormat.Channels);
            int framesPerMeasurement = Math.Max(
                1,
                reader.WaveFormat.SampleRate * SampleIntervalMilliseconds / 1000);
            int samplesPerMeasurement = framesPerMeasurement * channelCount;
            var samples = new float[samplesPerMeasurement];
            var bassFilterState = new float[channelCount];
            double lowPassAlpha = 1 - Math.Exp(
                -2 * Math.PI * 150 / reader.WaveFormat.SampleRate);
            var bassLevels = new List<float>();
            int samplesInWindow = 0;
            double bassSquare = 0;
            int bassSampleCount = 0;
            int samplesRead;

            while ((samplesRead = reader.Read(samples, 0, samples.Length)) > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                for (int i = 0; i < samplesRead; i++)
                {
                    float sample = samples[i];
                    if (float.IsFinite(sample))
                    {
                        sample = Math.Clamp(sample, -1, 1);
                        int channel = samplesInWindow % channelCount;
                        float bassSample = bassFilterState[channel] +
                                           (float)lowPassAlpha * (sample - bassFilterState[channel]);
                        bassFilterState[channel] = bassSample;
                        bassSquare += bassSample * bassSample;
                        bassSampleCount++;
                    }

                    samplesInWindow++;
                    if (samplesInWindow == samplesPerMeasurement)
                    {
                        bassLevels.Add(bassSampleCount > 0
                            ? (float)Math.Sqrt(bassSquare / bassSampleCount)
                            : 0);
                        samplesInWindow = 0;
                        bassSquare = 0;
                        bassSampleCount = 0;
                    }
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (bassSampleCount > 0)
            {
                bassLevels.Add((float)Math.Sqrt(bassSquare / bassSampleCount));
            }

            var analysis = new AudioAnalysisData(bassLevels.ToArray(), SampleIntervalMilliseconds);
            analysis.FileLength = fileLength;
            analysis.LastWriteUtcTicks = lastWriteUtcTicks;
            return analysis;
        }

        private static void WriteCache(
            string cachePath,
            AudioAnalysisData analysis,
            CancellationToken cancellationToken)
        {
            Directory.CreateDirectory(CacheDirectory);
            string temporaryPath = $"{cachePath}.{Guid.NewGuid():N}.tmp";
            using (var stream = new FileStream(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None))
            using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: false))
            {
                writer.Write(CacheVersion);
                writer.Write(analysis.FileLength);
                writer.Write(analysis.LastWriteUtcTicks);
                writer.Write(analysis.SampleIntervalMilliseconds);
                writer.Write(analysis.BassLevels.Length);
                foreach (float level in analysis.BassLevels)
                {
                    writer.Write(level);
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, cachePath, overwrite: true);
        }

        private static string GetCachePath(string audioPath)
        {
            string normalizedPath = Path.GetFullPath(audioPath);
            byte[] pathHash = SHA256.HashData(Encoding.UTF8.GetBytes(
                normalizedPath.ToUpperInvariant()));
            return Path.Combine(CacheDirectory, $"{Convert.ToHexString(pathHash)}.bin");
        }
    }

    public sealed class AudioAnalysisData
    {
        public AudioAnalysisData(float[] bassLevels, int sampleIntervalMilliseconds)
        {
            BassLevels = bassLevels;
            SampleIntervalMilliseconds = sampleIntervalMilliseconds;
        }

        public float[] BassLevels { get; }
        public int SampleIntervalMilliseconds { get; }
        internal long FileLength { get; set; }
        internal long LastWriteUtcTicks { get; set; }

        public float GetBassLevel(TimeSpan position)
        {
            if (BassLevels.Length == 0 || position <= TimeSpan.Zero)
            {
                return BassLevels.Length == 0 ? 0 : BassLevels[0];
            }

            double index = position.TotalMilliseconds / SampleIntervalMilliseconds;
            int lowerIndex = (int)Math.Min(index, BassLevels.Length - 1);
            int upperIndex = Math.Min(lowerIndex + 1, BassLevels.Length - 1);
            double fraction = Math.Clamp(index - lowerIndex, 0, 1);
            return (float)(BassLevels[lowerIndex] +
                           (BassLevels[upperIndex] - BassLevels[lowerIndex]) * fraction);
        }
    }
}
