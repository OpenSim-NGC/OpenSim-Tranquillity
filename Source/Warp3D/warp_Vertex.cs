using System;
using System.Collections;
using System.Collections.Generic;

namespace Warp3D
{
    /// <summary>
    /// Summary description for warp_Vertex.
    /// </summary>
    public class warp_Vertex
    {
        public warp_Object parent;

        public warp_Vector pos = new warp_Vector();   //(x,y,z) Coordinate of vertex
        public warp_Vector n = new warp_Vector();   //Normal Vector at vertex
        public float u = 0; // Texture x-coordinate (relative)
        public float v = 0; // Texture y-coordinate (relative)


        public warp_Vector pos2;  //Transformed vertex coordinate
        public warp_Vector n2;  //Transformed normal vector (camera space)
        public int x;  //Projected x coordinate
        public int y;  //Projected y coordinate
        public int z;  //Projected z coordinate for z-Buffer

        public float tx = 0; // Texture x-coordinate (relative)
        public float ty = 0; // Texture y-coordinate (relative)

        public int nx = 0; // Normal x-coordinate for envmapping
        public int ny = 0; // Normal y-coordinate for envmapping

        public float invZ = 1.0f;

        public bool visible = true;  //visibility tag for clipping
        public int clipcode = 0;
        public int id; // Vertex index

        //private Vector neighbor=new Vector(); //Neighbor triangles of vertex
        private List<warp_Triangle> neighbor = new List<warp_Triangle>();

        public warp_Vertex()
        {
            pos = new warp_Vector(0f, 0f, 0f);
        }

        public warp_Vertex(float xpos, float ypos, float zpos)
        {
            pos = new warp_Vector(xpos, ypos, zpos);
        }

        public warp_Vertex(float xpos, float ypos, float zpos, float u, float v)
        {
            pos = new warp_Vector(xpos, ypos, zpos);
            this.u = u;
            this.v = v;
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public warp_Vertex(warp_Vector ppos)
        {
            pos = ppos.getClone();
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public warp_Vertex(warp_Vector ppos, float u, float v)
        {
            pos = ppos.getClone();
            this.u = u;
            this.v = v;
        }

        public warp_Vertex(warp_Vector ppos, warp_Vector norm, float u, float v)
        {
            pos = ppos.getClone();
            n = norm.getClone();
            this.u = u;
            this.v = v;
        }

        public void project(warp_Matrix vertexProjection, warp_Matrix normalProjection, warp_Camera camera)
        // Projects this vertex into camera space
        {
            pos2 = pos.transform(vertexProjection);
            n2 = n.transform(normalProjection);

            if(pos2.z < 0.001f && pos2.z > -0.0001f)
                pos2.z = 0.001f;

            if(camera.isOrthographic)
            {
                x = (int)pos2.x;
                y = (int)pos2.y;
                invZ = -1.0f;
                tx = -u;
                ty = -v;
            }
            else
            {
                invZ = 1.0f / pos2.z;
                x = (int)(pos2.x * invZ + camera.halfscreenwidth);
                y = (int)(pos2.y * invZ + camera.halfscreenheight);
                invZ = - invZ;
                tx = u * invZ;
                ty = v * invZ;
            }

            z = (int)(65536f * pos2.z);
            nx = ((int)(n2.x * 127 + 127)) << 16;
            ny = ((int)(n2.y * 127 + 127)) << 16;
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public void setUV(float u, float v)
        {
            this.u = u;
            this.v = v;
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public void clipFrustrum(int w, int h)
        {
            // View plane clipping
            clipcode = 0;
            if(x < 0)
                clipcode |= 1;
            else if(x >= w)
                clipcode |= 2;
            if(y < 0)
                clipcode |= 4;
            else if(y >= h)
                clipcode |= 8;
            if(pos2.z < 0)
                clipcode |= 16;
            visible = (clipcode == 0);
        }

        public void registerNeighbor(warp_Triangle triangle)
        {
            if(!neighbor.Contains(triangle))
                neighbor.Add(triangle);
        }

        public void resetNeighbors()
        {
            neighbor.Clear();
        }

        public void regenerateNormal()
        {
            float nx = 0;
            float ny = 0;
            float nz = 0;
            for(int i = 0; i < neighbor.Count; ++i)
            {
                warp_Triangle tri = neighbor[i];
                warp_Vector wn = tri.rawNorm;
                nx += wn.x;
                ny += wn.y;
                nz += wn.z;
            }

            n = new warp_Vector(nx, ny, nz).normalize();
        }

        /*
                public void regenerateNormal()
                {
                    float nx=0;
                    float ny=0;
                    float nz=0;
                    Enumeration enum=neighbor.elements();
                    warp_Triangle tri;
                    warp_Vector wn;
                    while (enum.hasMoreElements())
                    {	
                        tri=(warp_Triangle)enum.nextElement();
                        wn=tri.getWeightedNormal();
                        nx+=wn.x;
                        ny+=wn.y;
                        nz+=wn.z;
                    }

                    n=new warp_Vector(nx,ny,nz).normalize();
                }
        */
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public void scaleTextureCoordinates(float fx, float fy)
        {
            u *= fx;
            v *= fy;
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public void moveTextureCoordinates(float fx, float fy)
        {
            u += fx;
            v += fy;
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public warp_Vertex getClone()
        {
            warp_Vertex newVertex = new warp_Vertex();
            newVertex.pos = pos.getClone();
            newVertex.n = n.getClone();
            newVertex.u = u;
            newVertex.v = v;

            return newVertex;
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public bool equals(warp_Vertex v)
        {
            return ((pos.x == v.pos.x) && (pos.y == v.pos.y) && (pos.z == v.pos.z));
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public bool equals(warp_Vertex v, float tolerance)
        {
            return Math.Abs(warp_Vector.sub(pos, v.pos).length()) < tolerance;
        }
    }
}

