using System;
using System.Collections;
using System.Collections.Generic;

namespace Warp3D
{
    /// <summary>
    /// Summary description for warp_RenderPipeline.
    /// </summary>
    public class warp_RenderPipeline: IDisposable
    {
        public warp_Screen screen;
        warp_Scene scene;
        public warp_Lightmap lightmap;

        public bool useId = false;

        warp_Rasterizer rasterizer;

        ArrayList transparentQueue = new ArrayList(16384);

        int zFar = int.MaxValue;

        public int[] zBuffer;

        public warp_RenderPipeline(warp_Scene scene, int w, int h)
        {
            this.scene = scene;

            screen = new warp_Screen(w, h);
            zBuffer = new int[screen.width * screen.height];
            rasterizer = new warp_Rasterizer(this);
        }

        public void buildLightMap()
        {
            if(lightmap == null)
            {
                lightmap = new warp_Lightmap(scene);
            }
            else
            {
                lightmap.rebuildLightmap();
            }

            rasterizer.loadLightmap(lightmap);
        }

        public void render(warp_Camera cam)
        {
            rasterizer.rebuildReferences(this);

            warp_Math.clearBuffer(zBuffer, zFar);
            //System.Array.Copy(screen.zBuffer,0,zBuffer,0,zBuffer.Length);

            if(scene.environment.background != null)
            {
                screen.drawBackground(scene.environment.background, 0, 0, screen.width, screen.height);
            }
            else
            {
                screen.clear(scene.environment.bgcolor);
            }

            cam.setScreensize(screen.width, screen.height);
            scene.prepareForRendering();
            emptyQueues();

            // Project
            warp_Matrix m = warp_Matrix.multiply(cam.getMatrix(), scene.matrix);
            warp_Matrix nm = warp_Matrix.multiply(cam.getNormalMatrix(), scene.normalmatrix);
            warp_Matrix vertexProjection, normalProjection;
            warp_Object obj;
            warp_Triangle t;
            warp_Vertex v;
            warp_Material objmaterial;
            const double log2inv = 1.4426950408889634073599246810019;
            int w = screen.width;
            int h = screen.height;
            int minx;
            int miny;
            int maxx;
            int maxy;

            for(int id = 0 ; id < scene.objects; ++id)
            {
                obj = scene.wobject[id];
                objmaterial = obj.material;
                if(objmaterial == null)
                    continue;
                if(!obj.visible)
                    continue;
                if(objmaterial.opaque && objmaterial.reflectivity == 0)
                    continue;

                vertexProjection = obj.matrix.getClone();
                normalProjection = obj.normalmatrix.getClone();
                vertexProjection.transform(m);
                normalProjection.transform(nm);
                minx = int.MaxValue;
                miny = int.MaxValue;
                maxx = int.MinValue;
                maxy = int.MinValue;

                for(int i = 0; i < obj.vertices; ++i)
                {
                    v = obj.fastvertex[i];
                    v.project(vertexProjection, normalProjection, cam);
                    v.clipFrustrum(w, h);
                    if(minx > v.x)
                        minx = v.x;
                    if(maxx < v.x)
                        maxx = v.x;
                    if(miny > v.y)
                        miny = v.y;
                    if(maxy < v.y)
                        maxy = v.y;
                }
                maxx -= minx;
                maxy -= miny;
                if(maxy > maxx)
                    maxx = maxy + 1;
                else
                    maxx++;

                obj.projectedmaxMips = (int)Math.Ceiling((Math.Log(maxx) * log2inv));
                obj.cacheMaterialData();
                if (objmaterial.opaque)
                {
                    rasterizer.loadFastMaterial(obj);
                    for (int i = 0; i < obj.triangles; ++i)
                    {
                        t = obj.fasttriangle[i];
                        t.project(normalProjection);
                        if (t.clipFrustrum(w, h))
                            rasterizer.render(t);
                    }
                }
                else
                {
                    for (int i = 0; i < obj.triangles; ++i)
                    {
                        t = obj.fasttriangle[i];
                        t.project(normalProjection);
                        if (t.clipFrustrum(w, h))
                            transparentQueue.Add(t);
                    }
                }
            }

            //screen.lockImage();

            warp_Triangle[] tri;
            obj = null;
            tri = getTransparentQueue();
            if(tri != null)
            {
                transparentQueue.Clear();
                for (int i = 0; i < tri.GetLength(0); i++)
                {
                    if(obj != tri[i].parent)
                    {
                        obj = tri[i].parent;
                        rasterizer.loadFastMaterial(obj);
                    }
                    rasterizer.render(tri[i]);
                }
            }

            //screen.unlockImage();
        }

        private void performResizing()
        {
            //screen.resize(requestedWidth, requestedHeight);
            zBuffer = new int[screen.width * screen.height];
        }

        // Triangle sorting
        private void emptyQueues()
        {
            transparentQueue.Clear();
        }

        private warp_Triangle[] getTransparentQueue()
        {
            if(transparentQueue.Count == 0)
                return null;

            IComparer comp = new tridistZDescCompare();
            transparentQueue.Sort(comp);
            warp_Triangle[] tri = new warp_Triangle[transparentQueue.Count];
            int id = 0;
            for(int i = 0; i < transparentQueue.Count; ++i)
                tri[id++] = (warp_Triangle)transparentQueue[i];

            //return sortTriangles(tri, 0, tri.GetLength(0) - 1);
            return tri;
        }

        private warp_Triangle[] sortTriangles(warp_Triangle[] tri, int L, int R)
        {
            //FIX: Added Index bounds checking. (Was causing random exceptions) - Created by: X
            if(L < 0)
                L = 0;
            if(L > tri.GetLength(0))
                L = tri.GetLength(0);
            if(R < 0)
                R = 0;
            if(R > tri.GetLength(0))
                R = tri.GetLength(0);
            // - Created by: X

            float m = (tri[L].distZ + tri[R].distZ) / 2;
            int i = L;
            int j = R;
            warp_Triangle temp;

            do
            {
                while(tri[i].distZ > m)
                    i++;
                while(tri[j].distZ < m)
                    j--;

                if(i <= j)
                {
                    temp = tri[i];
                    tri[i] = tri[j];
                    tri[j] = temp;
                    i++;
                    j--;
                }
            }
            while(j >= i);

            if(L < j)
                sortTriangles(tri, L, j);
            if(R > i)
                sortTriangles(tri, i, R);

            return tri;
        }

        public void Dispose()
        {
            if(screen != null)
                screen.Dispose();
            screen = null;
            zBuffer = null;
            rasterizer.clean();
            rasterizer = null;
            transparentQueue.Clear();
            transparentQueue = null;
        }
    }

    public class tridistZDescCompare : IComparer
    {
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        int IComparer.Compare(object x, object y)
        {
            float a = ((warp_Triangle)x).distZ;
            float b = ((warp_Triangle)y).distZ;
            if( a == b )
                return 0;
            return a < b ? 1 : -1;
        }
    }
}
