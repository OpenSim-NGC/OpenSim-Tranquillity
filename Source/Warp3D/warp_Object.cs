using System;
using System.Collections;
using System.Collections.Generic;

namespace Warp3D
{
    /// <summary>
    /// Summary description for warp_Object.
    /// </summary>
    public class warp_Object: warp_CoreObject
    {
        public Object userData = null;  // Can be freely used
        public String user = null;      // Can be freely used

        public List<warp_Vertex> vertexData = new List<warp_Vertex>(1024);
        public List<warp_Triangle> triangleData = new List<warp_Triangle>(1024);

        public int id;  // This object's index
        public String name = "";  // This object's name
        public bool visible = true; // Visibility tag
        public warp_Scene parent = null;
        private bool dirty = true;  // Flag for dirty handling

        public warp_Vertex[] fastvertex;
        public warp_Triangle[] fasttriangle;

        public int vertices = 0;
        public int triangles = 0;

        public warp_Material material = null;
        public int projectedmaxMips = 0;

        public int fastcolor;
        public int fastreflectivity;
        public int[] fastenvmappixels;
        public warp_Texture fasttexture;
        public int[] fasttpixels;
        public int fasttw;
        public int fastth;
        public int fasttbitw;
        public int fasttbith;
        public int fastmode;

        public warp_Object()
        {
        }

        public warp_Vertex vertex(int id)
        {
            return (warp_Vertex)vertexData[id];
        }

        public warp_Triangle triangle(int id)
        {
            return (warp_Triangle)triangleData[id];
        }

        public void addVertex(warp_Vertex newVertex)
        {
            newVertex.parent = this;
            vertexData.Add(newVertex);
            dirty = true;
        }

        public void addTriangle(warp_Triangle newTriangle)
        {
            newTriangle.parent = this;
            triangleData.Add(newTriangle);
            dirty = true;
        }

        public void addTriangle(int v1, int v2, int v3)
        {
            addTriangle(vertex(v1), vertex(v2), vertex(v3));
        }

        public void removeVertex(warp_Vertex v)
        {
            vertexData.Remove(v);
        }

        public void removeTriangle(warp_Triangle t)
        {
            triangleData.Remove(t);
        }

        public void removeVertexAt(int pos)
        {
            vertexData.RemoveAt(pos);
        }

        public void removeTriangleAt(int pos)
        {
            triangleData.RemoveAt(pos);
        }

        public void setMaterial(warp_Material m)
        {
            material = m;
        }

        public void rebuild()
        {
            if(!dirty)
                return;
            dirty = false;

            vertices = vertexData.Count;
            fastvertex = vertexData.ToArray();

            // Generate faster structure for triangles
            triangles = triangleData.Count;
            fasttriangle = triangleData.ToArray();

            for(int i = vertices - 1; i >= 0; i--)
            {
                fastvertex[i].id = i;
                fastvertex[i].resetNeighbors();
            }

            warp_Triangle tri;
            for(int i = triangles - 1; i >= 0; i--)
            {
                tri = fasttriangle[i];
                tri.id = i;
                tri.p1.registerNeighbor(tri);
                tri.p2.registerNeighbor(tri);
                tri.p3.registerNeighbor(tri);
            }

            regenerate();
        }

        /*
		public void rebuild()
		{
			if (!dirty) return;
			dirty=false;

			Enumeration enum;

			// Generate faster structure for vertices
			vertices=vertexData.size();
			vertex=new warp_Vertex[vertices];
			enum=vertexData.elements();
			for (int i=vertices-1;i>=0;i--) vertex[i]=(warp_Vertex)enum.nextElement();

			// Generate faster structure for triangles
			triangles=triangleData.size();
			triangle=new warp_Triangle[triangles];
			enum=triangleData.elements();
			for (int i=triangles-1;i>=0;i--)
			{
				triangle[i]=(warp_Triangle)enum.nextElement();
				triangle[i].id=i;
			}

			for (int i=vertices-1;i>=0;i--)
			{
				vertex[i].id=i;
				vertex[i].resetNeighbors();
			}

			warp_Triangle tri;
			for (int i=triangles-1;i>=0;i--)
			{
				tri=triangle[i];
				tri.p1.registerNeighbor(tri);
				tri.p2.registerNeighbor(tri);
				tri.p3.registerNeighbor(tri);
			}

			regenerate();
		}
		*/

        public void addVertex(float x, float y, float z)
        {
            addVertex(new warp_Vertex(x, y, z));
        }


        public void addVertex(float x, float y, float z, float u, float v)
        {
            warp_Vertex vert = new warp_Vertex(x, y, z);
            vert.setUV(u, v);
            addVertex(vert);
        }

        public void addTriangle(warp_Vertex a, warp_Vertex b, warp_Vertex c)
        {
            addTriangle(new warp_Triangle(a, b, c));
        }

        public void regenerate()
        // Regenerates the vertex normals
        {
            for(int i = 0; i < triangles; i++)
                fasttriangle[i].regenerateNormal();
            for(int i = 0; i < vertices; i++)
                fastvertex[i].regenerateNormal();
        }

        public void remapUV(int w, int h, float sx, float sy)
        {
            rebuild();
            for(int j = 0, p = 0; j < h; j++)
            {
                float v = ((float)j / (float)(h - 1)) * sy;
                for(int i = 0; i < w; i++)
                {
                    float u = ((float)i / (float)(w - 1)) * sx;
                    fastvertex[p++].setUV(u, v);
                }
            }
        }

        /*
		public void tilt(float fact)
		{
			rebuild();
			for (int i=0;i<vertices;i++)
				fastvertex[i].pos=warp_Vector.add(fastvertex[i].pos,warp_Vector.random(fact));

			regenerate();
		}
		*/

        public warp_Vector minimum()
        {
            if(vertices == 0)
                return new warp_Vector(0f, 0f, 0f);
            float minX = fastvertex[0].pos.x;
            float minY = fastvertex[0].pos.y;
            float minZ = fastvertex[0].pos.z;
            for(int i = 1; i < vertices; i++)
            {
                if(fastvertex[i].pos.x < minX)
                    minX = fastvertex[i].pos.x;
                if(fastvertex[i].pos.y < minY)
                    minY = fastvertex[i].pos.y;
                if(fastvertex[i].pos.z < minZ)
                    minZ = fastvertex[i].pos.z;
            }

            return new warp_Vector(minX, minY, minZ);
        }

        public warp_Vector maximum()
        {
            if(vertices == 0)
                return new warp_Vector(0f, 0f, 0f);
            float maxX = fastvertex[0].pos.x;
            float maxY = fastvertex[0].pos.y;
            float maxZ = fastvertex[0].pos.z;
            for(int i = 1; i < vertices; i++)
            {
                if(fastvertex[i].pos.x > maxX)
                    maxX = fastvertex[i].pos.x;
                if(fastvertex[i].pos.y > maxY)
                    maxY = fastvertex[i].pos.y;
                if(fastvertex[i].pos.z > maxZ)
                    maxZ = fastvertex[i].pos.z;
            }
            return new warp_Vector(maxX, maxY, maxZ);
        }


        public void detach()
        // Centers the object in its coordinate system
        // The offset from origin to object center will be transfered to the matrix,
        // so your object actually does not move.
        // Usefull if you want prepare objects for self rotation.
        {
            warp_Vector center = getCenter();

            for(int i = 0; i < vertices; i++)
            {
                fastvertex[i].pos.x -= center.x;
                fastvertex[i].pos.y -= center.y;
                fastvertex[i].pos.z -= center.z;
            }

            shift(center);
        }

        public warp_Vector getCenter()
        // Returns the center of this object
        {
            warp_Vector max = maximum();
            warp_Vector min = minimum();

            return new warp_Vector((max.x + min.x) / 2, (max.y + min.y) / 2, (max.z + min.z) / 2);
        }

        public warp_Vector getDimension()
        // Returns the x,y,z - Dimension of this object
        {
            warp_Vector max = maximum();
            warp_Vector min = minimum();

            return new warp_Vector(max.x - min.x, max.y - min.y, max.z - min.z);
        }

        public void matrixMeltdown()
        // Applies the transformations in the matrix to all vertices
        // and resets the matrix to untransformed.
        {
            rebuild();
            for(int i = vertices - 1; i >= 0; i--)
                fastvertex[i].pos = fastvertex[i].pos.transform(matrix);

            regenerate();
            matrix.reset();
            normalmatrix.reset();
        }

        public void destroy()
        {
            name = null;
            material = null;
            matrix = null;
            normalmatrix = null;
            parent = null;

            fastvertex = null;
            fasttriangle = null;
            vertexData.Clear();
            triangleData.Clear();
            vertexData = null;
            triangleData = null;
        }

        public warp_Object getClone()
        {
            warp_Object obj = new warp_Object();
            rebuild();
            for(int i = 0; i < vertices; i++)
                obj.addVertex(fastvertex[i].getClone());
            for(int i = 0; i < triangles; i++)
                obj.addTriangle(fasttriangle[i].getClone());
            obj.name = name + " [cloned]";
            obj.material = material;
            obj.matrix = matrix.getClone();
            obj.normalmatrix = normalmatrix.getClone();
            obj.rebuild();
            return obj;
        }

        private const int F = 0;      // FLAT
        private const int W = 1;  // WIREFRAME
        private const int P = 2;      // PHONG
        private const int E = 4;      // ENVMAP
        private const int T = 8;  // TEXTURED

        public void cacheMaterialData()
        {
            fastcolor = material.color;
            fastreflectivity = material.reflectivity;
            fasttexture = material.texture;
            if(material.envmap != null)
                fastenvmappixels = material.envmap.pixel;
            else
                fastenvmappixels = null;

            if(fasttexture != null)
            {
                if(projectedmaxMips < 2)
                {
                    fastcolor = warp_Color.multiply(fastcolor, fasttexture.averageColor);
                    fasttexture = null;
                }
                else
                {
                    int mip = projectedmaxMips - 2;
//                    int mip = fasttexture.maxmips;
                    if(mip < fasttexture.maxmips)
                    {
                        if(!fasttexture.hasmips)
                            fasttexture.GenMips();

                        fasttpixels = fasttexture.mips[mip];
                        fasttbitw = fasttexture.mipsBitWidth[mip];
                        fasttbith = fasttexture.mipsBitHeight[mip];
                        fasttw = fasttexture.mipstw[mip] - 1;
                        fastth = fasttexture.mipsth[mip] - 1;
                    }
                    else
                    {
                        fasttpixels = fasttexture.pixel;
                        fasttw = fasttexture.width - 1;
                        fastth = fasttexture.height - 1;
                        fasttbitw = fasttexture.bitWidth;
                        fasttbith = fasttexture.bitHeight;
                    }
                }
            }

            int m = 0;
            if(!material.flat)
                m |= P;
            if(fastenvmappixels != null)
                m |= E;
            if(fasttexture != null)
                m |= T;
            if(material.wireframe)
                m |= W;
            fastmode = m;
        }

        /*
				public void removeDuplicateVertices()
				{
					rebuild();
					Vector edgesToCollapse=new Vector();
					for (int i=0;i<vertices;i++)
						for (int j=i+1;j<vertices;j++)
							if (vertex[i].equals(vertex[j],0.0001f))
								edgesToCollapse.addElement(new warp_Edge(vertex[i],vertex[j]));


					Enumeration enum=edgesToCollapse.elements();
					while(enum.hasMoreElements()) 
					{
						edgeCollapse((warp_Edge)enum.nextElement());
					}

					removeDegeneratedTriangles();
				}

				public void removeDegeneratedTriangles()
				{
					rebuild();
					for (int i=0;i<triangles;i++)
						if (triangle[i].degenerated()) removeTriangleAt(i);

					dirty=true;
					rebuild();
				}

				private void edgeCollapse(warp_Edge edge)
				// Collapses the edge [u,v] by replacing v by u
				{
					warp_Vertex u=edge.start();
					warp_Vertex v=edge.end();
					if (!vertexData.contains(u)) return;
					if (!vertexData.contains(v)) return;
					rebuild();
					warp_Triangle tri;
					for (int i=0; i<triangles; i++)
					{
						tri=triangle(i);
						if (tri.p1==v) tri.p1=u;
						if (tri.p2==v) tri.p2=u;
						if (tri.p3==v) tri.p3=u;
					}
					vertexData.removeElement(v);
				}
				*/

    }
}
