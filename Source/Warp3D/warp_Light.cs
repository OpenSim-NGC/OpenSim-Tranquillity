using System;

namespace Warp3D
{
	/// <summary>
	/// Summary description for warp_Light.
	/// </summary>
	public class warp_Light
	{
		// F I E L D S

		public warp_Vector v; //Light direction
		public warp_Vector v2; //projected Light direction
		public int diffuse = 0;
		public int specular = 0;
		public int highlightSheen = 0;
		public int highlightSpread = 0;

		warp_Matrix matrix2;

		private warp_Light()
			// Default constructor not accessible
		{
		}

		public warp_Light(warp_Vector direction)
		{
			v = direction.getClone();
			v.normalize();
		}

		public warp_Light(warp_Vector direction, int diffuse)
		{
			v = direction.getClone();
			v.normalize();
			this.diffuse = diffuse;
		}

		public warp_Light(warp_Vector direction, int color, int highlightSheen, int highlightSpread)
		{
			v = direction.getClone();
			v.normalize();
			this.diffuse = color;
			this.specular = color;
			this.highlightSheen = highlightSheen;
			this.highlightSpread = highlightSpread;
		}

		public warp_Light(warp_Vector direction, int diffuse, int specular,int highlightSheen, int highlightSpread)
		{
			v = direction.getClone();
			v.normalize();
			this.diffuse = diffuse;
			this.specular = specular;
			this.highlightSheen = highlightSheen;
			this.highlightSpread = highlightSpread;
		}

		public void project(warp_Matrix m)
		{
			matrix2 = m.getClone();
			matrix2.transform(m);
			v2 = v.transform(matrix2);
		}
	}
}
