using System;

namespace Warp3D
{
	/// <summary>
	/// Summary description for warp_CoreObject.
	/// </summary>
	/// 

	public class warp_CoreObject
	{
		public warp_Matrix matrix=new warp_Matrix();
		public warp_Matrix normalmatrix=new warp_Matrix();

		public void transform(warp_Matrix m)
		{
			matrix.transform(m);
			normalmatrix.transform(m);
		}

		public void shift(float dx, float dy, float dz)
		{
			matrix.shift(dx,dy,dz);
		}

        public void shift( warp_Vector v )
		{
			matrix.shift(v.x,v.y,v.z);
		}

		public void scale(float d)
		{
			matrix.scale(d);
		}

		public void scale(float dx, float dy, float dz)
		{
			matrix.scale(dx,dy,dz);
		}

		public void scaleSelf(float d)
		{
			matrix.scaleSelf(d);
		}

		public void scaleSelf(float dx, float dy, float dz)
		{
			matrix.scaleSelf(dx,dy,dz);
		}

		public void rotate(warp_Vector d)
		{
			rotateSelf(d.x,d.y,d.z);
		}

		public void rotateSelf(warp_Vector d)
		{
			rotateSelf(d.x,d.y,d.z);
		}

		public void rotate(float dx, float dy, float dz)
		{
			matrix.rotate(dx,dy,dz);
			normalmatrix.rotate(dx,dy,dz);
		}

        public void rotate( warp_Quaternion quat, float x, float y, float z )
        {
            matrix.rotate( quat ,  x,  y,  z);
            normalmatrix.rotate( quat ,  x,  y,  z);
        }

        public void rotate( warp_Matrix m )
        {
            matrix.rotate( m );
            normalmatrix.rotate( m );
        }


		public void rotateSelf(float dx, float dy, float dz)
		{
			matrix.rotateSelf(dx,dy,dz);
			normalmatrix.rotateSelf(dx,dy,dz);
		}

        public void rotateSelf( warp_Quaternion quat )
        {
            matrix.rotateSelf( quat );
            normalmatrix.rotateSelf( quat );
        }

        public void rotateSelf( warp_Matrix m )
        {
            matrix.rotateSelf( m );
            normalmatrix.rotateSelf( m );
        }

        public void setPos( float x, float y, float z )
		{
			matrix.m03=x;
			matrix.m13=y;
			matrix.m23=z;
		}

		public void setPos(warp_Vector v)
		{
			setPos(v.x,v.y,v.z);
		}

		public warp_Vector getPos()
		{
			return new warp_Vector(matrix.m03,matrix.m13,matrix.m23);
		}
	}
}
