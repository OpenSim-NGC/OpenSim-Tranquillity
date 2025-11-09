using System;

namespace Warp3D
{
	public class warp_Matrix
	{
		public float m00=1, m01=0, m02=0, m03=0;
		public float m10=0, m11=1, m12=0, m13=0;
		public float m20=0, m21=0, m22=1, m23=0;
		public float m30=0, m31=0, m32=0, m33=1;

		public warp_Matrix(warp_Vector right, warp_Vector up, warp_Vector forward)
		{
			m00=right.x;
			m10=right.y;
			m20=right.z;
			m01=up.x;
			m11=up.y;
			m21=up.z;
			m02=forward.x;
			m12=forward.y;
			m22=forward.z;
		}



        public float this[ int column, int row ]
        {
            get
            {
                switch ( row )
                {
                    case 0:
                        if ( column == 0 ) return m00;
                        if ( column == 1 ) return m01;
                        if ( column == 2 ) return m02;
                        if ( column == 3 ) return m03;
                        break;
                    case 1:
                        if ( column == 0 ) return m11;
                        if ( column == 1 ) return m11;
                        if ( column == 2 ) return m12;
                        if ( column == 3 ) return m13;
                        break;
                    case 2:
                        if ( column == 0 ) return m20;
                        if ( column == 1 ) return m21;
                        if ( column == 2 ) return m22;
                        if ( column == 3 ) return m23;
                        break;
                    case 3:
                        if ( column == 0 ) return m30;
                        if ( column == 1 ) return m31;
                        if ( column == 2 ) return m32;
                        if ( column == 3 ) return m33;
                        break;

                    default:
                        return 0;
                }

                return 0;
            }
        }

		public warp_Matrix()
		{
			//
			// TODO: Add constructor logic here
			//
		}

		public static warp_Matrix shiftMatrix(float dx, float dy, float dz)
			// matrix for shifting
		{
			warp_Matrix m=new warp_Matrix();
			m.m03=dx;
			m.m13=dy;
			m.m23=dz;
			return m;
		}

        public static warp_Matrix quaternionMatrix( warp_Quaternion quat )
        {
            warp_Matrix m = new warp_Matrix();

   
            float xx = quat.X * quat.X;
            float xy = quat.X * quat.Y;
            float xz = quat.X * quat.Z;
            float xw = quat.X * quat.W;
            float yy = quat.Y * quat.Y;
            float yz = quat.Y * quat.Z;
            float yw = quat.Y * quat.W;
            float zz = quat.Z * quat.Z;
            float zw = quat.Z * quat.W;

            m.m00 = 1 - 2 * ( yy + zz );
            m.m01 = 2 * ( xy - zw) ;
            m.m02 = 2*(xz + yw);
            m.m10 = 2*(xy + zw);
            m.m11 = 1 - 2* ( xx + zz );
            m.m12 = 2*(yz - xw);
            m.m20 = 2*(xz - yw);
            m.m21 = 2*(yz + xw);
            m.m22 = 1 - 2 * ( xx + yy );

            m.m03 = m.m13 = m.m23 = m.m30 = m.m31 = m.m32 = 0;
            m.m33 = 1;
            
            return m;
        }

        public warp_Matrix rotateMatrix( warp_Quaternion quat )
        {

            reset();

            warp_Matrix temp = warp_Matrix.quaternionMatrix( quat );
            warp_Matrix result = warp_Matrix.multiply( this, temp );

			return result;
        }

		public static warp_Matrix scaleMatrix(float dx, float dy, float dz)
		{
			warp_Matrix m=new warp_Matrix();
			
			m.m00=dx;
			m.m11=dy;
			m.m22=dz;

			return m;
		}

		public static warp_Matrix scaleMatrix(float d)
		{
			return warp_Matrix.scaleMatrix(d,d,d);
		}

		public static warp_Matrix rotateMatrix(float dx, float dy, float dz)
		{
			warp_Matrix res=new warp_Matrix();

			float SIN;
			float COS;

			if (dx!=0)
			{
				warp_Matrix m = new warp_Matrix();
				SIN=warp_Math.sin(dx);
				COS=warp_Math.cos(dx);
				m.m11=COS;
				m.m12=SIN;
				m.m21=-SIN;
				m.m22=COS;
	
				res.transform(m);
			}
			if (dy!=0)
			{
				warp_Matrix m =new warp_Matrix();
				SIN=warp_Math.sin(dy);
				COS=warp_Math.cos(dy);
				m.m00=COS;
				m.m02=SIN;
				m.m20=-SIN;
				m.m22=COS;
				
				res.transform(m);
			}
			if (dz!=0)
			{
				warp_Matrix m =new warp_Matrix();
				SIN=warp_Math.sin(dz);
				COS=warp_Math.cos(dz);
				m.m00=COS;
				m.m01=SIN;
				m.m10=-SIN;
				m.m11=COS;
				
				res.transform(m);
			}

			return res;
		}

		public void shift(float dx, float dy, float dz)
		{
			m03 += dx;
			m13 += dy;
			m23 += dz;
		}

		public void scale(float dx, float dy, float dz)
		{
//			transform(scaleMatrix(dx,dy,dz));

			m00 *= dx;
			m01 *= dx;
			m02 *= dx;
			m03 *= dx;
			m10 *= dy;
			m11 *= dy;
			m12 *= dy;
			m13 *= dy;
			m20 *= dz;
			m21 *= dz;
			m22 *= dz;
			m23 *= dz;
		}

		public void scale(float d)
		{
			m00 *= d;
			m01 *= d;
			m02 *= d;
			m03 *= d;
			m10 *= d;
			m11 *= d;
			m12 *= d;
			m13 *= d;
			m20 *= d;
			m21 *= d;
			m22 *= d;
			m23 *= d;
		}

		public void rotate(float dx, float dy, float dz)
		{
			transform(rotateMatrix(dx,dy,dz));
		}

        public void rotate( warp_Quaternion quat, float x, float y, float z )
        {
            transform( rotateMatrix( quat ) );
        }

        public void rotate( warp_Matrix m )
        {
            transform( m );
        }

        public void scaleSelf( float dx, float dy, float dz )
		{
			m00 *= dx;
			m01 *= dy;
			m02 *= dz;
			m10 *= dx;
			m11 *= dy;
			m12 *= dz;
			m20 *= dx;
			m21 *= dy;
			m22 *= dz;
		}

		public void scaleSelf(float d)
		{
			m00 *= d;
			m01 *= d;
			m02 *= d;
			m10 *= d;
			m11 *= d;
			m12 *= d;
			m20 *= d;
			m21 *= d;
			m22 *= d;
		}

        public void shiftSelf( float dx, float dy, float dz )
        {
			m03 += m00 * dx + m01 * dy + m02 * dz;
			m13 += m10 * dx + m11 * dy + m12 * dz;
			m23 += m20 * dx + m21 * dx + m22 * dx;
        }

        public void rotateSelf( float dx, float dy, float dz )
        {
            preTransform( rotateMatrix( dx, dy, dz ) );
        }
        public void rotateSelf( warp_Matrix m  )
		{
			preTransform( m );
		}

        public void rotateSelf( warp_Quaternion quat )
        {
           preTransform(rotateMatrix( quat) );
        }


		public void transform(warp_Matrix n)
		{
			warp_Matrix m=this.getClone();

			m00 = n.m00*m.m00 + n.m01*m.m10 + n.m02*m.m20;
			m01 = n.m00*m.m01 + n.m01*m.m11 + n.m02*m.m21;
			m02 = n.m00*m.m02 + n.m01*m.m12 + n.m02*m.m22;
			m03 = n.m00*m.m03 + n.m01*m.m13 + n.m02*m.m23 + n.m03;
			m10 = n.m10*m.m00 + n.m11*m.m10 + n.m12*m.m20;
			m11 = n.m10*m.m01 + n.m11*m.m11 + n.m12*m.m21;
			m12 = n.m10*m.m02 + n.m11*m.m12 + n.m12*m.m22;
			m13 = n.m10*m.m03 + n.m11*m.m13 + n.m12*m.m23 + n.m13;
			m20 = n.m20*m.m00 + n.m21*m.m10 + n.m22*m.m20;
			m21 = n.m20*m.m01 + n.m21*m.m11 + n.m22*m.m21;
			m22 = n.m20*m.m02 + n.m21*m.m12 + n.m22*m.m22;
			m23 = n.m20*m.m03 + n.m21*m.m13 + n.m22*m.m23 + n.m23;
		}

		public void preTransform(warp_Matrix n)
		{
			warp_Matrix m=this.getClone();

			m00 = m.m00*n.m00 + m.m01*n.m10 + m.m02*n.m20;
			m01 = m.m00*n.m01 + m.m01*n.m11 + m.m02*n.m21;
			m02 = m.m00*n.m02 + m.m01*n.m12 + m.m02*n.m22;
			m03 = m.m00*n.m03 + m.m01*n.m13 + m.m02*n.m23 + m.m03;
			m10 = m.m10*n.m00 + m.m11*n.m10 + m.m12*n.m20;
			m11 = m.m10*n.m01 + m.m11*n.m11 + m.m12*n.m21;
			m12 = m.m10*n.m02 + m.m11*n.m12 + m.m12*n.m22;
			m13 = m.m10*n.m03 + m.m11*n.m13 + m.m12*n.m23 + m.m13;
			m20 = m.m20*n.m00 + m.m21*n.m10 + m.m22*n.m20;
			m21 = m.m20*n.m01 + m.m21*n.m11 + m.m22*n.m21;
			m22 = m.m20*n.m02 + m.m21*n.m12 + m.m22*n.m22;
			m23 = m.m20*n.m03 + m.m21*n.m13 + m.m22*n.m23 + m.m23;
		}

		public static warp_Matrix multiply(warp_Matrix m1, warp_Matrix m2)
		{
			warp_Matrix m=new warp_Matrix();

			m.m00 = m1.m00*m2.m00 + m1.m01*m2.m10 + m1.m02*m2.m20;
			m.m01 = m1.m00*m2.m01 + m1.m01*m2.m11 + m1.m02*m2.m21;
			m.m02 = m1.m00*m2.m02 + m1.m01*m2.m12 + m1.m02*m2.m22;
			m.m03 = m1.m00*m2.m03 + m1.m01*m2.m13 + m1.m02*m2.m23 + m1.m03;
			m.m10 = m1.m10*m2.m00 + m1.m11*m2.m10 + m1.m12*m2.m20;
			m.m11 = m1.m10*m2.m01 + m1.m11*m2.m11 + m1.m12*m2.m21;
			m.m12 = m1.m10*m2.m02 + m1.m11*m2.m12 + m1.m12*m2.m22;
			m.m13 = m1.m10*m2.m03 + m1.m11*m2.m13 + m1.m12*m2.m23 + m1.m13;
			m.m20 = m1.m20*m2.m00 + m1.m21*m2.m10 + m1.m22*m2.m20;
			m.m21 = m1.m20*m2.m01 + m1.m21*m2.m11 + m1.m22*m2.m21;
			m.m22 = m1.m20*m2.m02 + m1.m21*m2.m12 + m1.m22*m2.m22;
			m.m23 = m1.m20*m2.m03 + m1.m21*m2.m13 + m1.m22*m2.m23 + m1.m23;
			
			return m;
		}

/*
		public string toString()
		{
			// todo
		}
*/
		public warp_Matrix getClone()
		{
			warp_Matrix m=new warp_Matrix();

			m.m00=m00;  m.m01=m01;  m.m02=m02;  m.m03=m03;
			m.m10=m10;  m.m11=m11;  m.m12=m12;  m.m13=m13;
			m.m20=m20;  m.m21=m21;  m.m22=m22;  m.m23=m23;
			m.m30=m30;  m.m31=m31;  m.m32=m32;  m.m33=m33;
			
			return m;
		}

		public warp_Matrix inverse()
		{
			warp_Matrix m=new warp_Matrix();

			float q1 = m12;  float q6 = m10*m01;  float q7 = m10*m21;  float q8 = m02;
			float q13 = m20*m01;  float q14 = m20*m11;  float q21 = m02*m21;  float q22 = m03*m21;
			float q25 = m01*m12;  float q26 = m01*m13;  float q27 = m02*m11;  float q28 = m03*m11;
			float q29 = m10*m22;  float q30 = m10*m23;  float q31 = m20*m12;  float q32 = m20*m13;
			float q35 = m00*m22;  float q36 = m00*m23;  float q37 = m20*m02;  float q38 = m20*m03;
			float q41 = m00*m12;  float q42 = m00*m13;  float q43 = m10*m02;  float q44 = m10*m03;
			float q45 = m00*m11;  float q48 = m00*m21;
			float q49 = q45*m22-q48*q1-q6*m22+q7*q8;
			float q50 = q13*q1-q14*q8;
			float q51 = 1/(q49+q50);

			m.m00 = (m11*m22*m33-m11*m23*m32-m21*m12*m33+m21*m13*m32+m31*m12*m23-m31*m13*m22)*q51;
			m.m01 = -(m01*m22*m33-m01*m23*m32-q21*m33+q22*m32)*q51;
			m.m02 = (q25*m33-q26*m32-q27*m33+q28*m32)*q51;
			m.m03 = -(q25*m23-q26*m22-q27*m23+q28*m22+q21*m13-q22*m12)*q51;
			m.m10 = -(q29*m33-q30*m32-q31*m33+q32*m32)*q51;
			m.m11 = (q35*m33-q36*m32-q37*m33+q38*m32)*q51;
			m.m12 = -(q41*m33-q42*m32-q43*m33+q44*m32)*q51;
			m.m13 = (q41*m23-q42*m22-q43*m23+q44*m22+q37*m13-q38*m12)*q51;
			m.m20 = (q7*m33-q30*m31-q14*m33+q32*m31)*q51;
			m.m21 = -(q48*m33-q36*m31-q13*m33+q38*m31)*q51;
			m.m22 = (q45*m33-q42*m31-q6*m33+q44*m31)*q51;
			m.m23 = -(q45*m23-q42*m21-q6*m23+q44*m21+q13*m13-q38*m11)*q51;

			return m;
		}

		public void reset()
		{
			m00=1; m01=0; m02=0; m03=0;
			m10=0; m11=1; m12=0; m13=0;
			m20=0; m21=0; m22=1; m23=0;
			m30=0; m31=0; m32=0; m33=1;
		}
	}
}
