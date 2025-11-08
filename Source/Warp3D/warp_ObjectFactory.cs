using System;

namespace Warp3D
{
	/// <summary>
	/// Summary description for warp_ObjectFactory.
	/// </summary>
	public class warp_ObjectFactory
	{
		public static double pi = 3.1415926535;
		public static double deg2rad = pi / 180;

		public static warp_Object SIMPLEPLANE(float size, bool doubleSided)
		{
			warp_Object newObject = new warp_Object();

            newObject.addVertex( new warp_Vertex( -size, 0f, size, 0, 0 ) );
            newObject.addVertex( new warp_Vertex( size, 0f, size, 1f, 0 ) );
            newObject.addVertex( new warp_Vertex( size, 0f, -size, 1f, 1f ) );
            newObject.addVertex( new warp_Vertex( -size, 0f, -size, 0, 1f ) );

			newObject.addTriangle(0, 3, 2);
			newObject.addTriangle(0, 2, 1);

			if(doubleSided)
			{
				newObject.addTriangle(0, 2, 3);
				newObject.addTriangle(0, 1, 2);
			}

			return newObject;
		}

		public static warp_Object CUBE(float size)
		{
			return BOX(size, size, size);
		}

		public static warp_Object BOX(warp_Vector size)
		{
			return BOX(size.x, size.y, size.z);
		}

		public static warp_Object BOX(float xsize, float ysize, float zsize)
		{
			float x = (float) Math.Abs(xsize / 2);
			float y = (float) Math.Abs(ysize / 2);
			float z = (float) Math.Abs(zsize / 2);

			float xx, yy, zz;

			warp_Object n = new warp_Object();
			int[] xflag = new int[6];
			int[] yflag = new int[6];
			int[] zflag = new int[6];

			xflag[0] = 10;
			yflag[0] = 3;
			zflag[0] = 0;
			xflag[1] = 10;
			yflag[1] = 15;
			zflag[1] = 3;
			xflag[2] = 15;
			yflag[2] = 3;
			zflag[2] = 10;
			xflag[3] = 10;
			yflag[3] = 0;
			zflag[3] = 12;
			xflag[4] = 0;
			yflag[4] = 3;
			zflag[4] = 5;
			xflag[5] = 5;
			yflag[5] = 3;
			zflag[5] = 15;

			for (int side = 0; side < 6; side++)
			{
				for (int i = 0; i < 4; i++)
				{
					xx = ( (xflag[side] & (1 << i)) > 0) ? x : -x;
					yy = ( (yflag[side] & (1 << i)) > 0) ? y : -y;
					zz = ( (zflag[side] & (1 << i)) > 0) ? z : -z;
					n.addVertex(xx, yy, zz, i & 1, (i & 2) >> 1);
				}
				int t = side << 2;
				n.addTriangle(t, t + 2, t + 3);
				n.addTriangle(t, t + 3, t + 1);
			}

			return n;
		}

		public static warp_Object CONE(float height, float radius, int segments)
		{
			warp_Vector[] path = new warp_Vector[4];
			float h = height / 2;
			path[0] = new warp_Vector(0, h, 0);
			path[1] = new warp_Vector(radius, -h, 0);
			path[2] = new warp_Vector(radius, -h, 0);
			path[3] = new warp_Vector(0, -h, 0);

			return ROTATIONOBJECT(path, segments);
		}

		public static warp_Object CYLINDER(float height, float radius,int segments)
		{
			warp_Vector[] path = new warp_Vector[6];
			float h = height / 2;
			path[0] = new warp_Vector(0, h, 0);
			path[1] = new warp_Vector(radius, h, 0);
			path[2] = new warp_Vector(radius, h, 0);
			path[3] = new warp_Vector(radius, -h, 0);
			path[4] = new warp_Vector(radius, -h, 0);
			path[5] = new warp_Vector(0, -h, 0);

			return ROTATIONOBJECT(path, segments);
		}

		public static warp_Object SPHERE(float radius, int segments)
		{
			warp_Vector[] path = new warp_Vector[segments];

			float x, y, angle;

			path[0] = new warp_Vector(0, radius, 0);
			path[segments - 1] = new warp_Vector(0, -radius, 0);

			for (int i = 1; i < segments - 1; i++)
			{
				angle = - ( ( (float) i / (float) (segments - 2)) - 0.5f) *
					3.14159265f;
				x = (float) Math.Cos(angle) * radius;
				y = (float) Math.Sin(angle) * radius;
				path[i] = new warp_Vector(x, y, 0);
			}

			return ROTATIONOBJECT(path, segments);
		}

		public static warp_Object ROTATIONOBJECT(warp_Vector[] path, int sides)
		{
			int steps = sides + 1;
			warp_Object newObject = new warp_Object();
			double alpha = 2 * pi / ( (double) steps - 1);
			float qx, qz;
			int nodes = path.GetLength(0);
			warp_Vertex vertex = null;
			float u, v; // Texture coordinates

			for (int j = 0; j < steps; j++)
			{
				u = (float) (steps - j - 1) / (float) (steps - 1);
				for (int i = 0; i < nodes; i++)
				{
					v = (float) i / (float) (nodes - 1);
					qx = (float) (path[i].x * Math.Cos(j * alpha) +
						path[i].z * Math.Sin(j * alpha));
					qz = (float) (path[i].z * Math.Cos(j * alpha) -
						path[i].x * Math.Sin(j * alpha));
					vertex = new warp_Vertex(qx, path[i].y, qz);
					vertex.u = u;
					vertex.v = v;
					newObject.addVertex(vertex);
				}
			}

			for (int j = 0; j < steps - 1; j++)
			{
				for (int i = 0; i < nodes - 1; i++)
				{
					newObject.addTriangle(i + nodes * j, i + nodes * (j + 1),
						i + 1 + nodes * j);
					newObject.addTriangle(i + nodes * (j + 1),
						i + 1 + nodes * (j + 1),
						i + 1 + nodes * j);

				}
			}

			for (int i = 0; i < nodes - 1; i++)
			{
				newObject.addTriangle(i + nodes * (steps - 1), i,
					i + 1 + nodes * (steps - 1));
				newObject.addTriangle(i, i + 1, i + 1 + nodes * (steps - 1));
			}
			return newObject;

		}

		public static warp_Object TORUSKNOT(float p, float q, float r_tube,float r_out, float r_in, float h,int segments, int steps)
		{
			float x, y, z, r, t, theta;

			warp_Vector[] path = new warp_Vector[segments + 1];
			for (int i = 0; i < segments + 1; i++)
			{
				t = 2 * 3.14159265f * i / (float) segments;
				r = r_out + r_in * warp_Math.cos(p * t);
				z = h * warp_Math.sin(p * t);
				theta = q * t;
				x = r * warp_Math.cos(theta);
				y = r * warp_Math.sin(theta);
				path[i] = new warp_Vector(x, y, z);
			}
			return TUBE(path, r_tube, steps, true);
		}

		public static warp_Object SPIRAL(float h, float r_out, float r_in,
			float r_tube, float w, float f,
			int segments, int steps)
		{
			float x, y, z, r, t, theta;

			warp_Vector[] path = new warp_Vector[segments + 1];
			for (int i = 0; i < segments + 1; i++)
			{
				t = (float) i / (float) segments;
				r = r_out + r_in * warp_Math.sin(2 * 3.14159265f * f * t);
				z = (h / 2) + h * t;
				theta = 2 * 3.14159265f * w * t;
				x = r * warp_Math.cos(theta);
				y = r * warp_Math.sin(theta);
				path[i] = new warp_Vector(x, y, z);
			}
			return TUBE(path, r_tube, steps, false);
		}

		public static warp_Object TUBE(warp_Vector[] path, float r, int steps, bool closed)
		{
			warp_Vector[] circle = new warp_Vector[steps];
			float angle;
			for (int i = 0; i < steps; i++)
			{
				angle = 2 * 3.14159265f * (float) i / (float) steps;
				circle[i] = new warp_Vector(r * warp_Math.cos(angle),
					r * warp_Math.sin(angle), 0f);
			}

			warp_Object newObject = new warp_Object();
			int segments = path.GetLength(0);
			warp_Vector forward, up, right;
			warp_Matrix frenetmatrix;
			warp_Vertex tempvertex;
			float relx, rely;
			int a, b, c, d;

			for (int i = 0; i < segments; i++)
			{
				// Calculate frenet frame matrix

				if (i != segments - 1)
				{
					forward = warp_Vector.sub(path[i + 1], path[i]);
				}
				else
				{
					if (!closed)
					{
						forward = warp_Vector.sub(path[i], path[i - 1]);
					}
					else
					{
						forward = warp_Vector.sub(path[1], path[0]);
					}
				}

				forward.normalize();
				up = new warp_Vector(0f, 0f, 1f);
				right = warp_Vector.getNormal(forward, up);
				up = warp_Vector.getNormal(forward, right);
				frenetmatrix = new warp_Matrix(right, up, forward);
				frenetmatrix.shift(path[i].x, path[i].y, path[i].z);

				// Add nodes

				relx = (float) i / (float) (segments - 1);
				for (int k = 0; k < steps; k++)
				{
					rely = (float) k / (float) steps;
					tempvertex = new warp_Vertex(circle[k].transform(frenetmatrix));
					tempvertex.u = relx;
					tempvertex.v = rely;
					newObject.addVertex(tempvertex);
				}
			}

			for (int i = 0; i < segments - 1; i++)
			{
				for (int k = 0; k < steps - 1; k++)
				{
					a = i * steps + k;
					b = a + 1;
					c = a + steps;
					d = b + steps;
					newObject.addTriangle(a, c, b);
					newObject.addTriangle(b, c, d);
				}
				a = (i + 1) * steps - 1;
				b = a + 1 - steps;
				c = a + steps;
				d = b + steps;
				newObject.addTriangle(a, c, b);
				newObject.addTriangle(b, c, d);
			}

			return newObject;
		}
	}
}
