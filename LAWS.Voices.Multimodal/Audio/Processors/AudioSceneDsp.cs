using System;
using System.Numerics;
using System.Threading;

namespace LAWS.Voices.Multimodal.Audio.Processors
{
    public static class AudioSceneDsp
    {
        public static int EnsurePowerOfTwo(int value)
        {
            value = Math.Max(32, value);
            int p = 1;
            while (p < value)
            {
                p <<= 1;
            }
            return p;
        }

        public static float[] CreateHannWindow(int size)
        {
            var window = new float[size];
            if (size <= 1)
            {
                if (size == 1) window[0] = 1f;
                return window;
            }

            for (int i = 0; i < size; i++)
            {
                window[i] = 0.5f * (1f - (float) Math.Cos((2.0 * Math.PI * i) / (size - 1)));
            }

            return window;
        }

        public static float[] ExtractChannel(float[] interleaved, int channels, int channelIndex)
        {
            channels = Math.Max(1, channels);
            channelIndex = Math.Clamp(channelIndex, 0, channels - 1);
            if (channels == 1)
            {
                return (float[]) interleaved.Clone();
            }

            int frames = interleaved.Length / channels;
            var output = new float[frames];
            for (int i = 0; i < frames; i++)
            {
                output[i] = interleaved[(i * channels) + channelIndex];
            }

            return output;
        }

        public static float[] ToMono(float[] interleaved, int channels)
        {
            channels = Math.Max(1, channels);
            if (channels == 1)
            {
                return (float[]) interleaved.Clone();
            }

            int frames = interleaved.Length / channels;
            var mono = new float[frames];
            for (int i = 0; i < frames; i++)
            {
                float sum = 0f;
                int baseIndex = i * channels;
                for (int c = 0; c < channels; c++)
                {
                    sum += interleaved[baseIndex + c];
                }
                mono[i] = sum / channels;
            }

            return mono;
        }

        public static Complex[] ForwardRealFft(float[] frame, float[]? window = null)
        {
            int size = EnsurePowerOfTwo(frame.Length);
            var buffer = new Complex[size];
            for (int i = 0; i < frame.Length; i++)
            {
                double sample = frame[i] * (window != null && i < window.Length ? window[i] : 1.0f);
                buffer[i] = new Complex(sample, 0.0);
            }

            FFT(buffer, inverse: false);
            return buffer;
        }

        public static float[] InverseRealFft(Complex[] spectrum)
        {
            var buffer = (Complex[]) spectrum.Clone();
            FFT(buffer, inverse: true);
            var output = new float[buffer.Length];
            for (int i = 0; i < buffer.Length; i++)
            {
                output[i] = (float) buffer[i].Real;
            }
            return output;
        }

        public static float[,] ComputeLogSpectrogram(float[] mono, int sampleRate, int windowSize, int hopSize, int outputBands, CancellationToken cancellationToken = default)
        {
            windowSize = EnsurePowerOfTwo(windowSize);
            hopSize = Math.Max(1, hopSize);
            outputBands = Math.Max(16, outputBands);

            if (mono.Length == 0)
            {
                return new float[0, 0];
            }

            int frameCount = Math.Max(1, 1 + Math.Max(0, mono.Length - windowSize) / hopSize);
            int bins = windowSize / 2;
            var window = CreateHannWindow(windowSize);
            var matrix = new float[frameCount, outputBands];
            double maxFrequency = Math.Max(2000.0, sampleRate / 2.0);
            double minFrequency = 60.0;

            for (int frameIndex = 0; frameIndex < frameCount; frameIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                int start = frameIndex * hopSize;
                var frame = new float[windowSize];
                int copyLength = Math.Min(windowSize, Math.Max(0, mono.Length - start));
                if (copyLength > 0)
                {
                    Array.Copy(mono, start, frame, 0, copyLength);
                }

                var fft = ForwardRealFft(frame, window);
                for (int band = 0; band < outputBands; band++)
                {
                    double t0 = band / (double) outputBands;
                    double t1 = (band + 1) / (double) outputBands;
                    double f0 = minFrequency * Math.Pow(maxFrequency / minFrequency, t0);
                    double f1 = minFrequency * Math.Pow(maxFrequency / minFrequency, t1);
                    int b0 = Math.Clamp((int) Math.Floor(f0 / sampleRate * windowSize), 1, bins - 1);
                    int b1 = Math.Clamp((int) Math.Ceiling(f1 / sampleRate * windowSize), b0, bins - 1);

                    double sum = 0.0;
                    int count = 0;
                    for (int b = b0; b <= b1; b++)
                    {
                        sum += fft[b].Magnitude;
                        count++;
                    }

                    double magnitude = count > 0 ? sum / count : 0.0;
                    matrix[frameIndex, band] = (float) (20.0 * Math.Log10(magnitude + 1e-6));
                }
            }

            return matrix;
        }

        public static double WrapPhase(double phase)
        {
            while (phase > Math.PI) phase -= Math.PI * 2.0;
            while (phase < -Math.PI) phase += Math.PI * 2.0;
            return phase;
        }

        public static double FrequencyToMidi(double frequency)
        {
            return frequency <= 0.0 ? 0.0 : 69.0 + (12.0 * Math.Log(frequency / 440.0, 2.0));
        }

        private static void FFT(Complex[] buffer, bool inverse)
        {
            int n = buffer.Length;
            int bits = (int) Math.Round(Math.Log2(n));
            for (int i = 0; i < n; i++)
            {
                int j = ReverseBits(i, bits);
                if (j > i)
                {
                    (buffer[i], buffer[j]) = (buffer[j], buffer[i]);
                }
            }

            for (int len = 2; len <= n; len <<= 1)
            {
                double angle = (inverse ? 2.0 : -2.0) * Math.PI / len;
                Complex wLen = new(Math.Cos(angle), Math.Sin(angle));
                for (int i = 0; i < n; i += len)
                {
                    Complex w = Complex.One;
                    int half = len >> 1;
                    for (int j = 0; j < half; j++)
                    {
                        Complex u = buffer[i + j];
                        Complex v = buffer[i + j + half] * w;
                        buffer[i + j] = u + v;
                        buffer[i + j + half] = u - v;
                        w *= wLen;
                    }
                }
            }

            if (inverse)
            {
                for (int i = 0; i < n; i++)
                {
                    buffer[i] /= n;
                }
            }
        }

        private static int ReverseBits(int value, int bitCount)
        {
            int reversed = 0;
            for (int i = 0; i < bitCount; i++)
            {
                reversed = (reversed << 1) | (value & 1);
                value >>= 1;
            }
            return reversed;
        }
    }
}
