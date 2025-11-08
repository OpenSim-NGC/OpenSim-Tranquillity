using System;

namespace Warp3D
{
    public class warp_Vector
    {
        public float x = 0;      //Cartesian (default)
        public float y = 0;      //Cartesian (default)
        public float z = 0;      //Cartesian (default),Cylindric
        public float r = 0;      //Cylindric
        public float theta = 0;  //Cylindric

        public warp_Vector()
        {
        }

        public warp_Vector(float xpos, float ypos, float zpos)
        {
            x = xpos;
            y = ypos;
            z = zpos;
        }
        public warp_Vector normalize()
        // Normalizes the vector
        {
            float dist = length();
            if(dist == 0)
                return this;
            float invdist = 1 / dist;
            x *= invdist;
            y *= invdist;
            z *= invdist;
            return this;
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public warp_Vector reverse()
        // Reverses the vector
        {
            x = -x;
            y = -y;
            z = -z;
            return this;
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public float length()
        // Lenght of this vector
        {
            return (float)Math.Sqrt(x * x + y * y + z * z);
        }

        public warp_Vector transform(warp_Matrix m)
        // Modifies the vector by matrix m
        {
            float newx = x * m.m00 + y * m.m01 + z * m.m02 + m.m03;
            float newy = x * m.m10 + y * m.m11 + z * m.m12 + m.m13;
            float newz = x * m.m20 + y * m.m21 + z * m.m22 + m.m23;

            return new warp_Vector(newx, newy, newz);
        }

        public warp_Vector pointtransform(warp_Matrix m)
        // Modifies the point by matrix m
        {
            float newx = x * m.m00 + y * m.m01 + z * m.m02 + m.m03;
            float newy = x * m.m10 + y * m.m11 + z * m.m12 + m.m13;
            float newz = x * m.m20 + y * m.m21 + z * m.m22 + m.m23;
            float w =    x * m.m30 + y * m.m31 + z * m.m32 + m.m33;
            if(w != 1.0f && w != 0f)
            {
                w = 1.0f / w;
                newx *= w;
                newy *= w;
                newy *= w;
            }

            return new warp_Vector(newx, newy, newz);
        }

        public void buildCylindric()
        // Builds the cylindric coordinates out of the given cartesian coordinates
        {
            r = (float)Math.Sqrt(x * x + y * y);
            theta = (float)Math.Atan2(x, y);
        }

        public void buildCartesian()
        // Builds the cartesian coordinates out of the given cylindric coordinates
        {
            x = r * warp_Math.cos(theta);
            y = r * warp_Math.sin(theta);
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public static warp_Vector getNormal(warp_Vector a, warp_Vector b)
        // returns the normal vector of the plane defined by the two vectors
        {
            return vectorProduct(a, b).normalize();
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public static warp_Vector getNormal(warp_Vector a, warp_Vector b, warp_Vector c)
        // returns the normal vector of the plane defined by the two vectors
        {
            return vectorProduct(a, b, c).normalize();
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public static warp_Vector vectorProduct(warp_Vector a, warp_Vector b)
        // returns a x b
        {
            return new warp_Vector(a.y * b.z - b.y * a.z, a.z * b.x - b.z * a.x, a.x * b.y - b.x * a.y);
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public static warp_Vector vectorProduct(warp_Vector a, warp_Vector b, warp_Vector c)
        // returns (b-a) x (c-a)
        {
            return vectorProduct(sub(b, a), sub(c, a));
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public static float vectorsCos(warp_Vector a, warp_Vector b)
        // returns the angle between 2 vectors
        {
            a.normalize();
            b.normalize();
            return (a.x * b.x + a.y * b.y + a.z * b.z);
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public static float Dot(warp_Vector a, warp_Vector b)
        // returns the angle between 2 vectors
        {
            return (a.x * b.x + a.y * b.y + a.z * b.z);
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public static warp_Vector add(warp_Vector a, warp_Vector b)
        // adds 2 vectors
        {
            return new warp_Vector(a.x + b.x, a.y + b.y, a.z + b.z);
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public static warp_Vector sub(warp_Vector a, warp_Vector b)
        // substracts 2 vectors
        {
            return new warp_Vector(a.x - b.x, a.y - b.y, a.z - b.z);
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public static warp_Vector scale(float f, warp_Vector a)
        // substracts 2 vectors
        {
            return new warp_Vector(f * a.x, f * a.y, f * a.z);
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public static float len(warp_Vector a)
        // length of vector
        {
            return (float)Math.Sqrt(a.x * a.x + a.y * a.y + a.z * a.z);
        }

        /*
		public static warp_Vector random(float fact)
			// returns a random vector
		{
			return new warp_Vector(fact*warp_Math.random(),fact*warp_Math.random(),fact*warp_Math.random());
		}

		public String toString()
		{
			return new String ("<vector x="+x+" y="+y+" z="+z+">\r\n");
		}
*/

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public warp_Vector getClone()
        {
            return new warp_Vector(x, y, z);
        }
    }
}
