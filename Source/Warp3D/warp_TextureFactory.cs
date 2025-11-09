using System;

namespace Warp3D
{
	/// <summary>
	/// Summary description for warp_TextureFactory.
	/// </summary>
	public class warp_TextureFactory
	{
		public  static float pi = 3.1415926535f;
		public  static float deg2rad = pi / 180;
		private static float[,] noiseBuffer;
		private static bool noiseBufferInitialized = false;
		public static int ALPHA = unchecked((int)0xFF000000); // alpha mask

		private warp_TextureFactory()
		{
		}

		public static warp_Texture SKY(int w, int h, float density)
		{
			int[] colors = new int[2];
			colors[0] = 0x003399;
			colors[1] = 0xFFFFFF;
			return PERLIN(w, h, 0.5f, 2.8f * density, 8, 1024).colorize(warp_Color.makeGradient(colors, 1024));
		}

		public static warp_Texture MARBLE(int w, int h, float density)
		{
			int[] colors = new int[3];
			colors[0] = 0x111111;
			colors[1] = 0x696070;
			colors[2] = 0xFFFFFF;
			return WAVE(w, h, 0.5f, 0.64f * density, 6, 1024).colorize(warp_Color.makeGradient(colors, 1024));
		}

		public static warp_Texture WOOD(int w, int h, float density)
		{
			int[] colors = new int[3];
			colors[0] = 0x332211;
			colors[1] = 0x523121;
			colors[2] = 0x996633;

			return GRAIN(w, h, 0.5f, 3f * density, 3, 8, 1024).colorize(warp_Color.makeGradient(colors, 1024));
		}

		public static warp_Texture PERLIN(int w, int h, float persistency,
			float density, int samples, int scale)
		{
			initNoiseBuffer();
			warp_Texture t = new warp_Texture(w, h);
			int pos = 0;
			float wavelength = (float) ( (w > h) ? w : h) / density;

			for (int y = 0; y < h; y++)
			{
				for (int x = 0; x < w; x++)
				{
					t.pixel[pos++] = (int) ( (float) scale *
						perlin2d(x, y, wavelength, persistency,
						samples));
				}
			}
			return t;
		}

		public static warp_Texture WAVE(int w, int h, float persistency,
			float density, int samples, int scale)
		{
			initNoiseBuffer();
			warp_Texture t = new warp_Texture(w, h);
			int pos = 0;
			float wavelength = (float) ( (w > h) ? w : h) / density;

			for (int y = 0; y < h; y++)
			{
				for (int x = 0; x < w; x++)
				{
					t.pixel[pos++] = (int) ( (double) scale *
						(Math.Sin(32 *
						perlin2d(x, y, wavelength,
						persistency, samples)) * 0.5 + 0.5));
				}
			}
			return t;
		}

		public static warp_Texture GRAIN(int w, int h, float persistency,
			float density, int samples, int levels,
			int scale)
			// TIP: For wooden textures
		{
			initNoiseBuffer();
			warp_Texture t = new warp_Texture(w, h);
			int pos = 0;
			float wavelength = (float) ( (w > h) ? w : h) / density;
			float perlin;

			for (int y = 0; y < h; y++)
			{
				for (int x = 0; x < w; x++)
				{
					perlin = (float) levels *
						perlin2d(x, y, wavelength, persistency, samples);
					t.pixel[pos++] = (int) ( (float) scale *
						(perlin - (float) (int) perlin));
				}
			}
			return t;
		}

		// Perlin noise functions

		private static float perlin2d(float x, float y, float wavelength,
			float persistence, int samples)
		{
			float sum = 0;
			float freq = 1f / wavelength;
			float amp = persistence;
			float range = 0;

			for (int i = 0; i < samples; i++)
			{
				sum += amp * interpolatedNoise(x * freq, y * freq, i);
				range += amp;
				amp *= persistence;
				freq *= 2;
			}
			return warp_Math.crop(sum / persistence * 0.5f + 0.5f, 0, 1);
		}

		// Helper methods

		private static float interpolatedNoise(float x, float y, int octave)
		{
			int intx = (int) x;
			int inty = (int) y;
			float fracx = x - (float) intx;
			float fracy = y - (float) inty;

			float i1 = warp_Math.interpolate(noise(intx, inty, octave),
				noise(intx + 1, inty, octave), fracx);
			float i2 = warp_Math.interpolate(noise(intx, inty + 1, octave),
				noise(intx + 1, inty + 1, octave),
				fracx);

			return warp_Math.interpolate(i1, i2, fracy);
		}

		private static float smoothNoise(int x, int y, int o)
		{
			return (noise(x - 1, y - 1, o) + noise(x + 1, y - 1, o) +
				noise(x - 1, y + 1, o) + noise(x + 1, y + 1, o)) / 16
				+
				(noise(x - 1, y, o) + noise(x + 1, y, o) + noise(x, y - 1, o) +
				noise(x, y + 1, o)) / 8
				+ noise(x, y, o) / 4;
		}

		private static float noise(int x, int y, int octave)
		{
			return noiseBuffer[octave & 3, (x + y * 57) & 8191];
		}

		private static float noise(int seed, int octave)
		{
			int id = octave & 3;
			int n = (seed << 13) ^ seed;

			if (id == 0)
			{
				return (float) (1f -
					( (n * (n * n * 15731 + 789221) + 1376312589) &
					0x7FFFFFFF) *
					0.000000000931322574615478515625f);
			}
			if (id == 1)
			{
				return (float) (1f -
					( (n * (n * n * 12497 + 604727) + 1345679039) &
					0x7FFFFFFF) *
					0.000000000931322574615478515625f);
			}
			if (id == 2)
			{
				return (float) (1f -
					( (n * (n * n * 19087 + 659047) + 1345679627) &
					0x7FFFFFFF) *
					0.000000000931322574615478515625f);
			}
			return (float) (1f -
				( (n * (n * n * 16267 + 694541) + 1345679501) &
				0x7FFFFFFF) *
				0.000000000931322574615478515625f);
		}

		private static void initNoiseBuffer()
		{
			if (noiseBufferInitialized)
			{
				return;
			}

			noiseBuffer = new float[4,8192];
			for (int octave = 0; octave < 4; octave++)
			{
				for (int i = 0; i < 8192; i++)
				{
					noiseBuffer[octave,i] = noise(i, octave);
				}
			}
			noiseBufferInitialized = true;
		}
		
		public static warp_Texture CHECKERBOARD(int w, int h, int cellbits, int oddColor, int evenColor)
		{
			warp_Texture t = new warp_Texture(w, h);

			int pos = 0;
			for (int y = 0; y < h; y++)
			{
				for (int x = 0; x < w; x++)
				{
					int c = ( ( (x >> cellbits) + (y >> cellbits)) & 1) == 0 ? evenColor : oddColor;
					t.pixel[pos++] =  c;
				}
			}

			return t;
		}
	}
}
