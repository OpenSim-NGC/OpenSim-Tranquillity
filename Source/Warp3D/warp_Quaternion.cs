using System;
using System.Collections;
using System.Text;
using System.Diagnostics;	

namespace Warp3D
{
    public class warp_Quaternion
    {
        public float X;
        public float Y;
        public float Z;
        public float W;

        public warp_Quaternion()
        {
        }

        public warp_Quaternion( float x, float y, float z, float w )
        {
            this.X = x;
            this.Y = y;
            this.Z = z;
            this.W = w;
        }

        public warp_Quaternion getClone()
        {
            return new warp_Quaternion( this.X, this.Y, this.Z, this.W );
        }

        static public warp_Quaternion matrix( warp_Matrix xfrm )
        {
            warp_Quaternion quat = new warp_Quaternion();
            // Check the sum of the diagonal
            float tr = xfrm[ 0, 0 ] + xfrm[ 1, 1 ] + xfrm[ 2, 2 ];
            if ( tr > 0.0f )
            {
                // The sum is positive
                // 4 muls, 1 div, 6 adds, 1 trig function call
                float s = ( float )Math.Sqrt( tr + 1.0f );
                quat.W = s * 0.5f;
                s = 0.5f / s;
                quat.X = ( xfrm[ 1, 2 ] - xfrm[ 2, 1 ] ) * s;
                quat.Y = ( xfrm[ 2, 0 ] - xfrm[ 0, 2 ] ) * s;
                quat.Z = ( xfrm[ 0, 1 ] - xfrm[ 1, 0 ] ) * s;
            }
            else
            {
                // The sum is negative
                // 4 muls, 1 div, 8 adds, 1 trig function call
                int[] nIndex = { 1, 2, 0 };
                int i, j, k;
                i = 0;
                if ( xfrm[ 1, 1 ] > xfrm[ i, i ] )
                    i = 1;
                if ( xfrm[ 2, 2 ] > xfrm[ i, i ] )
                    i = 2;
                j = nIndex[ i ];
                k = nIndex[ j ];

                float s = ( float )Math.Sqrt( ( xfrm[ i, i ] - ( xfrm[ j, j ] + xfrm[ k, k ] ) ) + 1.0f );
                quat[ i ] = s * 0.5f;
                if ( s != 0.0 )
                {
                    s = 0.5f / s;
                }
                quat[ j ] = ( xfrm[ i, j ] + xfrm[ j, i ] ) * s;
                quat[ k ] = ( xfrm[ i, k ] + xfrm[ k, i ] ) * s;
                quat[ 3 ] = ( xfrm[ j, k ] - xfrm[ k, j ] ) * s;
            }

            return quat;
        }

        public float this[ int index ]
        {
            get
            {
                Debug.Assert( 0 <= index && index <= 3 );
                if ( index <= 1 )
                {
                    if ( index == 0 )
                    {
                        return this.X;
                    }
                    return this.Y;
                }
                if ( index == 2 )
                {
                    return this.Z;
                }
                return this.W;
            }
            set
            {
                Debug.Assert( 0 <= index && index <= 3 );
                if ( index <= 1 )
                {
                    if ( index == 0 )
                    {
                        this.X = value;
                    }
                    else
                    {
                        this.Y = value;
                    }
                }
                else
                {
                    if ( index == 2 )
                    {
                        this.Z = value;
                    }
                    else
                    {
                        this.W = value;
                    }
                }
            }
        }
    }
}
