using System;

namespace Warp3D
{
    /// <summary>
    /// Summary description for warp_Math.
    /// </summary>
    public class warp_Math
    {
        //		private static float[] sinus;
        //		private static float[] cosinus;
        //		private static bool trig=false;
        public static float pi = 3.1415926535f;
        //		private static float rad2scale=4096f/3.14159265f/2f;
        //		private static float pad=256*3.14159265f;

        private static int[] fastRandoms;
        private static int fastRndPointer = 0;
        private static bool fastRndInit = false;

        private static Random _rnd = new Random();


        public warp_Math()
        {
            //
            // TODO: Add constructor logic here
            //
        }

        public static float interpolate(float a, float b, float d)
        {
            float f = (1 - cos(d * pi)) * 0.5f;
            return a + f * (b - a);
        }


        public static float random()
        {
            Random rnd = new Random();
            return (float)(rnd.NextDouble() * 2 - 1);
        }

        public static float random(float min, float max)
        {
            //Random rnd = new Random();
            return (float)(_rnd.NextDouble() * (max - min) + min);
        }

        public static float randomWithDelta(float averidge, float delta)
        {
            return averidge + random() * delta;
        }

        public static int fastRnd(int bits)
        {
            if (bits < 1)
            {
                return 0;
            }
            fastRndPointer = (fastRndPointer + 1) & 31;
            if (!fastRndInit)
            {
                fastRandoms = new int[32];
                for (int i = 0; i < 32; i++)
                {
                    fastRandoms[i] = (int)random(0, 0xFFFFFF);
                }
                fastRndInit = true;
            }
            return fastRandoms[fastRndPointer] & (1 << (bits - 1));
        }

        public static int fastRndBit()
        {
            return fastRnd(1);
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public static float deg2rad(float deg)
        {
            return deg * 0.0174532925194f;
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public static float rad2deg(float rad)
        {
            return rad * 57.295779514719f;
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public static float sin(float angle)
        {
            return (float)Math.Sin(angle);
            //			if(!trig) buildTrig();
            //			return sinus[(int)((angle+pad)*rad2scale)&0xFFF];
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public static float cos(float angle)
        {
            return (float)Math.Cos(angle);
            //			if(!trig) buildTrig();
            //			return cosinus[(int)((angle+pad)*rad2scale)&0xFFF];
        }
        /*
                private static void buildTrig()
                {
                    sinus=new float[4096];
                    cosinus=new float[4096];

                    for (int i=0;i<4096;i++)
                    {
                        sinus[i]=(float)Math.Sin((float)i/rad2scale);
                        cosinus[i]=(float)Math.Cos((float)i/rad2scale);
                    }

                    trig=true;
                }
        */
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public static float pythagoras(float a, float b)
        {
            return (float)Math.Sqrt(a * a + b * b);
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public static int pythagoras(int a, int b)
        {
            return (int)Math.Sqrt(a * a + b * b);
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public static int crop(int num, int min, int max)
        {
            return (num < min) ? min : (num > max) ? max : num;
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public static float crop(float num, float min, float max)
        {
            return (num < min) ? min : (num > max) ? max : num;
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public static bool inrange(int num, int min, int max)
        {
            return ((num >= min) && (num < max));
        }

        unsafe public static void clearBuffer(int[] buffer, int c)
        {
            /*
            for (int idx = 0; idx < buffer.Length; idx++)
            {
                buffer[idx] = c;
            }
            */

            fixed (int* pC = buffer)
            {
                int* pClear = pC;
                for (int i = 0; i < buffer.GetLength(0) / 4; i++)
                {
                    *pClear++ = c;
                    *pClear++ = c;
                    *pClear++ = c;
                    *pClear++ = c;
                }
            }
        }

        unsafe public static void clearBuffer(long[] buffer, int c)
        {
            /*  for (int idx = 0; idx < buffer.Length; idx++)
             {
                 buffer[idx] = c;
             }*/
            fixed (long* pC = buffer)
            {
                long* pClear = pC;
                for (int i = 0; i < buffer.GetLength(0) / 4; i++)
                {
                    *pClear++ = c;
                    *pClear++ = c;
                    *pClear++ = c;
                    *pClear++ = c;
                }
            }
        }

        public static void clearBuffer(byte[] buffer, byte value)
        {
            System.Array.Clear(buffer, 0, buffer.GetLength(0));
        }

        public static void cropBuffer(int[] buffer, int min, int max)
        {
            for (int i = buffer.GetLength(0) - 1; i >= 0; i--) buffer[i] = crop(buffer[i], min, max);
        }

        public static void copyBuffer(int[] source, int[] target)
        {
            System.Array.Copy(source, 0, target, 0, crop(source.GetLength(0), 0, target.GetLength(0)));
        }

        /*
		public static float random()
		{
			return (float)(Math.r.random()*2-1);
		}

		public static float random(float min, float max)
		{
			return (float)(Math.random()*(max-min)+min);
		}

		public static float randomWithDelta(float averidge, float delta)
		{
			return averidge+random()*delta;
		}
		*/
    }
}
