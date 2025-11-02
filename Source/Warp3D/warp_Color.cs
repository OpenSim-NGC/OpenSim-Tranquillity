using System;
using System.Runtime.InteropServices;

namespace Warp3D
{
    /// <summary>
    /// Summary description for warp_Color.
    /// </summary>
    public class warp_Color
    {
        public static int MASKALPHA = unchecked((int)0xFF000000); // alpha mask
        public static int MASKHALFALPHA = unchecked((int)0x7F000000); // alpha mask
        public static int MASKRED = unchecked((int)0xFF0000);  // red mask
        public static int MASKGREEN = unchecked((int)0xFF00);  // green mask
        public static int MASKBLUE = unchecked((int)0xFF);  // blue mask
        public static int MASKRB = unchecked((int)0x00FF00FF);  // mask for additive/subtractive shading
        public static int MASKAG = unchecked((int)0xFF00FF00);  // mask for additive/subtractive shading
        public static int RBHALF = unchecked((int)0x00800080);
        public static int MASKRBHALF = unchecked((int)0xFF80FF80);
        public static int MASKRBOVER = unchecked((int)0x01000100);  
        public static int MASK7Bit = unchecked((int)0x00FEFEFF);  // mask for additive/subtractive shading
        public static int MASK6Bit = unchecked((int)0x00FCFCFC);  // mask for additive/subtractive shading
        public static int MASK7BitA = unchecked((int)0x00FEFEFF);  // mask for additive/subtractive shading
        public static int MASK6BitA = unchecked((int)0x00FCFCFC);  // mask for additive/subtractive shading
        public static int MASKRGB = unchecked((int)0x00FFFFFF);  // rgb mask

        public static int White = unchecked((int)0xFFFFFFFF);
        public static int Grey = unchecked((int)0xFF7F7F7F);
        public static int Black = unchecked((int)0xFF000000);
        private uint argb;
        public readonly byte a = 0;
        public readonly byte r = 0;
        public readonly byte g = 0;
        public readonly byte b = 0;

        public warp_Color(byte r, byte g, byte b, byte a)
        {
            this.r = r;
            this.g = g;
            this.b = b;
            this.a = a;
        }

        public warp_Color(uint argb)
        {
                        // Arrange
            this.a = (byte)((argb >> 24) & 0xFF);
            this.r = (byte)((argb >> 16) & 0xFF);
            this.g = (byte)((argb >> 8) & 0xFF);
            this.b = (byte)(argb & 0xFF);
        }

        public warp_Color()
        {
        }

        public static int random(int color, int delta)
        {
            Random rnd = new Random();

            int r = (color >> 16) & 0xff;
            int g = (color >> 8) & 0xff;
            int b = color & 0xff;

            r += (int)(rnd.NextDouble() * (float)delta);
            g += (int)(rnd.NextDouble() * (float)delta);
            b += (int)(rnd.NextDouble() * (float)delta);

            return getCropColor(r, g, b);
        }

        public static int random()
        {
            Random rnd = new Random();
            return (int)(rnd.NextDouble() * 16777216);
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public static int getAlpha(int c)
        {
            return (c >> 24) & 0xff;
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public static int getRed(int c)
        {
            return (c >> 16) & 0xff;
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public static int getGreen(int c)
        {
            return (c >> 8) & 0xff;
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public static int getBlue(int c)
        {
            return c & 0xff;
        }

        public static int[] makeGradient(int[] colors, int size)
        {
            int[] pal = new int[size];
            int c1, c2, pos1, pos2, range;
            int r, g, b, r1, g1, b1, r2, g2, b2, dr, dg, db;
            if(colors.GetLength(0) == 1)
            {
                c1 = colors[0];
                for(int i = 0; i < size; i++)
                {
                    pal[i] = c1;
                }
                return pal;
            }

            for(int c = 0; c < colors.GetLength(0) - 1; c++)
            {
                c1 = colors[c];
                c2 = colors[c + 1];
                pos1 = size * c / (colors.GetLength(0) - 1);
                pos2 = size * (c + 1) / (colors.GetLength(0) - 1);
                range = pos2 - pos1;
                r1 = warp_Color.getRed(c1) << 16;
                g1 = warp_Color.getGreen(c1) << 16;
                b1 = warp_Color.getBlue(c1) << 16;
                r2 = warp_Color.getRed(c2) << 16;
                g2 = warp_Color.getGreen(c2) << 16;
                b2 = warp_Color.getBlue(c2) << 16;
                dr = (r2 - r1) / range;
                dg = (g2 - g1) / range;
                db = (b2 - b1) / range;
                r = r1;
                g = g1;
                b = b1;
                for(int i = pos1; i < pos2; i++)
                {
                    pal[i] = getColor(r >> 16, g >> 16, b >> 16);
                    r += dr;
                    g += dg;
                    b += db;
                }
            }

            return pal;
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public static int add(int color1, int color2)
        {
            int t1,t2,t3,t4;

            t1 = color1 & MASKRB;
            t2 = color2 & MASKRB;
            t3 = t1 + t2;
            t3 |= MASKRBOVER - ((t3 >> 8) & MASKRB);
            t3 &= MASKRB;

            t1 = (color1 >> 8) & MASKRB;
            t2 = (color2 >> 8) & MASKRB;
            t4 = t1 + t2;
            t4 |= MASKRBOVER - ((t4 >> 8) & MASKRB);
            t4 &= MASKRB;

            return (t4 << 8) | t3;
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public static int avg2c(int color1, int color2)
        {
            int t1,t2,t3,t4;

            t1 = color1 & MASKRB;
            t2 = color2 & MASKRB;
            t3 = t1 + t2 + 0x00010001;
            t3 >>= 1;
            t3 &= MASKRB;

            t1 = (color1 >> 8) & MASKRB;
            t2 = (color2 >> 8) & MASKRB;
            t4 = t1 + t2 + 0x00010001;
            t4 >>= 1;
            t4 &= MASKRB;

            return (t4 << 8) | t3;
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public static int avg4c(int color1, int color2, int color3, int color4)
        {
            int t1,t2,t3,t4;

            t1 = color1 & MASKRB;
            t2 = color2 & MASKRB;
            t3 = t1 + t2;
            t1 = color3 & MASKRB;
            t2 = color4 & MASKRB;
            t3 += t1 + t2 + 0x00020002;
            t3 >>= 2;
            t3 &= MASKRB;

            t1 = (color1 >> 8) & MASKRB;
            t2 = (color2 >> 8) & MASKRB;
            t4 = t1 + t2 + 0x00020002;;
            t1 = (color3 >> 8) & MASKRB;
            t2 = (color4 >> 8) & MASKRB;
            t4 += t1 + t2;
            t4 >>= 2;
            t4 &= MASKRB;

            return (t4 << 8) | t3;
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public static int sub(int color1, int color2)
        // Substracts color2 from color1
        {

            int t1,t2,t3,t4;

            t1 = color1 & MASKRB;
            t2 = (~color2) & MASKRB;
            t3 = t1 + t2;
            t3 |= MASKRBOVER - ((t3 >> 8) & MASKRB);
            t3 &= MASKRB;

            t1 = (color1 >> 8) & MASKRB;
            t2 = ((~color2) >> 8) & MASKRB;
            t4 = t1 + t2;
            t4 |= MASKRBOVER - ((t4 >> 8) & MASKRB);
            t4 &= MASKRB;

            return (t4 << 8) | t3;

/*
            int pixel = (color1 & MASK7Bit) + (~color2 & MASK7Bit);
            int overflow = ~pixel & 0x1010100;
            overflow = overflow - (overflow >> 8);
            return ALPHA | (~overflow & pixel);
*/
        }
/*
        public static int subneg(int color1, int color2)
        // Substracts the negative of color2 from color1
        {
            int pixel = (color1 & MASK7Bit) + (color2 & MASK7Bit);
            int overflow = ~pixel & 0x1010100;
            overflow = overflow - (overflow >> 8);
            return ALPHA | (~overflow & pixel);
        }

        public static int inv(int color)
        // returns the inverse of the given color
        {
            color = (~color) &  RGB;
            return ALPHA | color;
        }
*/
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public static int mix(int color1, int color2)
        // Returns the averidge color from 2 colors
        {
            return MASKALPHA | (((color1 & MASK7Bit) >> 1) + ((color2 & MASK7Bit) >> 1));
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public static int scale(int color, int factor)
        {
            if(factor == 255)
                return color;

            if(factor == 0)
                return 0;

            int a = color & MASKALPHA;

            if(factor == 127)
                return a | ((color >> 1) & 0x7F7F7F);

            int RB = (((color & MASKRB) * factor) >> 8) & MASKRB;
            int b = (((color & MASKBLUE) * factor) >> 8) & MASKBLUE;
            return a | RB | b;
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public static int multiply(int color1, int color2)
        {
            int t1, t2, t3, t4;

            t1 = color1;
            t2 = color2;

            t3 = (t1 & 0xff) * (t2 & 0xff); // b on g
            t3 |= (t1 & MASKRED) * ((t2 >> 16) & 0xff); // r on a
            t3 += RBHALF; // add halfbit
            t3 = (t3 + ((t3 >> 8) & MASKRB)) >> 8; // in rb place
            t3 &= MASKRB;

            t1 = (color1 >> 8) & MASKRB;
            t2 = (color2 >> 8) & MASKRB;

            t4 = (t1 & 0xff) * (t2 & 0xff); // g on g
            t4 |= (t1 & MASKRED) * ((t2 >> 16) & 0xff); // a on a
            t4 += RBHALF; // add halfbit
            t4 = (t4 + ((t4 >> 8) & MASKRB));
            t4 &= MASKAG;

            return t3 | t4;
/*
            int a;
            int r = (color1 >> 24 ) & 0xff;
            int g = (color2 >> 24 ) & 0xff;
            if( r == 0xff && g == 0xff)
                a = ALPHA;
            else
            {
                a = ((r * g)) & 0xff00;
                a <<= 16;
            }

            if((color1 & RGB) == 0 && (color2 & RGB) == 0)
                return a;

            r = unchecked((((color1 >> 16) & 0xff) * ((color2 >> 16) & 0xff)) & 0xff00);
            g = (((color1 >> 8) & 0xff) * ((color2 >> 8) & 0xff)) & 0xff00;
            int b = ((color1 & 0xff) * (color2 & 0xff)) >> 8;
            return a | r << 8 | g | b;
*/
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public static int transparency(int bkgrd, int color, int alpha)
        {
            if(alpha == 0)
            {
                return bkgrd;
            }
            if(alpha == 255)
            {
                return color;
            }
            if(alpha == 127)
            {
                return mix(bkgrd, color);
            }

            int colorRB = bkgrd & 0xff00ff;
            colorRB += (((color & 0xff00ff) - colorRB) * alpha) >> 8;
            colorRB &= 0xff00ff;

            int colorg = bkgrd & 0x00ff00;
            colorg += (((color & 0x00ff00) - colorg) * alpha) >> 8;
            colorg &= 0x00ff00;

            return MASKALPHA | colorRB | colorg;
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        // blends color alpha, but result image is solid
        public static int overSolid(int bkgrd, int color)
        {
            int alpha = color & MASKALPHA;
            if(alpha == 0)
            {
                return bkgrd;
            }
            if(alpha == Black)
            {
                return color;
            }
            if(alpha == MASKHALFALPHA)
            {
                return mix(bkgrd, color);
            }
            alpha = (alpha >> 24) & 0xff;
            int colorRB = bkgrd & MASKRB;
            colorRB += (((color & MASKRB) - colorRB) * alpha) >> 8;
            colorRB &= 0xff00ff;

            int colorg = bkgrd & MASKGREEN;
            colorg += (((color & MASKGREEN) - colorg) * alpha) >> 8;
            colorg &= MASKGREEN;

            return MASKALPHA | colorRB | colorg;
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public static int getCropColor(int r, int g, int b)
        {
            return MASKALPHA | (warp_Math.crop(r, 0, 255) << 16) | (warp_Math.crop(g, 0, 255) << 8) | warp_Math.crop(b, 0, 255);
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public static int getCropColor(int r, int g, int b, int a)
        {
            return (warp_Math.crop(a , 0, 255) << 24) |
                    (warp_Math.crop(r, 0, 255) << 16) |
                    (warp_Math.crop(g, 0, 255) << 8) |
                    warp_Math.crop(b, 0, 255);
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public static int getColor(int r, int g, int b)
        {
            return MASKALPHA | (r << 16) | (g << 8) | b;
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public static int getColor(int r, int g, int b, int a)
        {
            return (a << 24) | (r << 16) | (g << 8) | b;
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public static int getGray(int color)
        {
            int r = ((color & MASKRED) >> 16);
            int g = ((color & MASKGREEN) >> 8);
            int b = (color & MASKBLUE);
            int Y = (r * 3 + g * 6 + b) / 10;
            return (color & MASKALPHA) | (Y << 16) | (Y << 8) | Y;
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public static int getAverage(int color)
        {
            return (((color & MASKRED) >> 16) + ((color & MASKGREEN) >> 8) + (color & MASKBLUE)) / 3;
        }
    }
}
