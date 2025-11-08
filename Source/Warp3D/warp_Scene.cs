using System;
using System.Collections;
using System.Timers;
using SkiaSharp;

namespace Warp3D
{
    /// <summary>
    /// Summary description for warp_Scene.
    /// </summary>
    public class warp_Scene : warp_CoreObject
    {
        public static String version = "1.0.0";
        public static String release = "0010";

        public warp_RenderPipeline renderPipeline;
        public int width, height;

        public warp_Environment environment = new warp_Environment();
        public warp_Camera defaultCamera = warp_Camera.FRONT();

        public warp_Object[] wobject;
        public warp_Light[] light;

        public int objects = 0;
        public int lights = 0;

        private bool objectsNeedRebuild = true;
        private bool lightsNeedRebuild = true;

        protected bool preparedForRendering = false;

        public warp_Vector normalizedOffset = new warp_Vector(0f, 0f, 0f);
        public float normalizedScale = 1f;

        public Hashtable objectData = new Hashtable();
        public Hashtable lightData = new Hashtable();
        public Hashtable materialData = new Hashtable();
        public Hashtable cameraData = new Hashtable();

        public warp_Scene()
        {
        }

        public warp_Scene(int w, int h)
        {
            width = w;
            height = h;
            renderPipeline = new warp_RenderPipeline(this, w, h);
            defaultCamera.setScreensize(w, h);
        }

        public void destroy()
        {
            objects = objectData.Count;
            foreach (warp_Object o in objectData.Values)
                o.destroy();

            objectData.Clear();
            lightData.Clear();
            materialData.Clear();
            cameraData.Clear();
            if (renderPipeline != null)
                renderPipeline.Dispose();
            renderPipeline = null;
            environment = null;
            defaultCamera = null;
            wobject = null;
        }

        public void removeAllObjects()
        {

            objectData = new Hashtable();
            objectsNeedRebuild = true;
            rebuild();
        }

        public void rebuild()
        {
            if (objectsNeedRebuild)
            {
                objectsNeedRebuild = false;

                objects = objectData.Count;
                wobject = new warp_Object[objects];
                IDictionaryEnumerator enumerator = objectData.GetEnumerator();

                for (int i = 0; i < objects; ++i)
                {
                    enumerator.MoveNext();
                    wobject[i] = (warp_Object)enumerator.Value;

                    wobject[i].id = i;
                    wobject[i].rebuild();
                }
            }

            if (lightsNeedRebuild)
            {
                lightsNeedRebuild = false;
                lights = lightData.Count;
                light = new warp_Light[lights];
                IDictionaryEnumerator enumerator = lightData.GetEnumerator();
                for (int i = lights - 1; i >= 0; i--)
                {
                    enumerator.MoveNext();
                    light[i] = (warp_Light)enumerator.Value;

                }
            }
        }

        public warp_Object sceneobject(String key)
        {
            return (warp_Object)objectData[key];
        }

        public warp_Material material(String key)
        {
            return (warp_Material)materialData[key];
        }

        public warp_Camera camera(String key)
        {
            return (warp_Camera)cameraData[key];
        }

        public void addObject(String key, warp_Object obj)
        {
            obj.name = key;
            objectData.Add(key, obj);
            obj.parent = this;
            objectsNeedRebuild = true;
            preparedForRendering = false;
        }

        public void removeObject(String key)
        {
            objectData.Remove(key);
            objectsNeedRebuild = true;
            preparedForRendering = false;
        }

        public void addMaterial(String key, warp_Material m)
        {
            materialData.Add(key, m);
        }

        public void removeMaterial(String key)
        {
            materialData.Remove(key);
        }

        public void addCamera(String key, warp_Camera c)
        {
            cameraData.Add(key, c);
        }

        public void removeCamera(String key)
        {
            cameraData.Remove(key);
        }

        public void addLight(String key, warp_Light l)
        {
            lightData.Add(key, l);
            lightsNeedRebuild = true;
        }

        public void removeLight(String key)
        {
            lightData.Remove(key);
            lightsNeedRebuild = true;
            preparedForRendering = false;
        }

        public void prepareForRendering()
        {
            if (preparedForRendering)
                return;
            preparedForRendering = true;

            rebuild();
            renderPipeline.buildLightMap();
        }

        public void render()
        {
            renderPipeline.render(this.defaultCamera);
        }

        public object getImage()
        {
            // return a bitmap-like object; may be SKBitmap when using the source build
            return renderPipeline.screen.getImage();
        }

        public void setBackgroundColor(int bgcolor)
        {
            environment.bgcolor = bgcolor | warp_Color.MASKALPHA; // needs to be solid for now
        }

        public void setBackground(warp_Texture t)
        {
            environment.setBackground(t);
        }

        public void setAmbient(int ambientcolor)
        {
            environment.ambient = ambientcolor;
        }

        public int countVertices()
        {
            int counter = 0;
            for (int i = 0; i < objects; i++) counter += wobject[i].vertices;
            return counter;
        }

        public int countTriangles()
        {
            int counter = 0;
            for (int i = 0; i < objects; i++) counter += wobject[i].triangles;
            return counter;
        }

        public void normalize()
        {
            objectsNeedRebuild = true;
            rebuild();

            warp_Vector min, max, tempmax, tempmin;
            if (objects == 0)
            {
                return;
            }

            matrix = new warp_Matrix();
            normalmatrix = new warp_Matrix();

            max = wobject[0].maximum();
            min = wobject[0].maximum();

            for (int i = 0; i < objects; i++)
            {
                tempmax = wobject[i].maximum();
                tempmin = wobject[i].maximum();
                if (tempmax.x > max.x)
                {
                    max.x = tempmax.x;
                }
                if (tempmax.y > max.y)
                {
                    max.y = tempmax.y;
                }
                if (tempmax.z > max.z)
                {
                    max.z = tempmax.z;
                }
                if (tempmin.x < min.x)
                {
                    min.x = tempmin.x;
                }
                if (tempmin.y < min.y)
                {
                    min.y = tempmin.y;
                }
                if (tempmin.z < min.z)
                {
                    min.z = tempmin.z;
                }
            }
            float xdist = max.x - min.x;
            float ydist = max.y - min.y;
            float zdist = max.z - min.z;
            float xmed = (max.x + min.x) / 2;
            float ymed = (max.y + min.y) / 2;
            float zmed = (max.z + min.z) / 2;

            float diameter = (xdist > ydist) ? xdist : ydist;
            diameter = (zdist > diameter) ? zdist : diameter;

            normalizedOffset = new warp_Vector(xmed, ymed, zmed);
            normalizedScale = 2 / diameter;

            shift(normalizedOffset.reverse());
            scale(normalizedScale);
        }

        public float EstimateBoxProjectedArea(warp_Vector pos, warp_Vector size, warp_Matrix rotation)
        {
            warp_Matrix om = new warp_Matrix();
            om.scale(size.x, size.y, size.z);
            om.transform(rotation);

            float xmax;
            float ymax;
            /*
            if (defaultCamera.isOrthographic)
            {
                xmax = Math.Abs(om.m00);
                ymax = Math.Abs(om.m22);
                if (xmax < 1f || ymax < 1f)
                    return -1;
                return xmax * ymax / (width * height);
            }
            */
            om.m03 = pos.x;
            om.m13 = pos.y;
            om.m23 = pos.z;
            warp_Vector side;
            warp_Vector v;
            float xmin;
            float ymin;

            warp_Matrix m = warp_Matrix.multiply(defaultCamera.getMatrix(), matrix);
            om.transform(m);

            float zmin;
            side = new warp_Vector(-1f, -1f, -1f);
            v = side.transform(om);
            xmin = v.x;
            xmax = xmin;
            ymin = v.y;
            ymax = ymin;
            zmin = v.z;

            side.x = 1f;
            v = side.transform(om);
            if (xmin > v.x)
                xmin = v.x;
            else if (xmax < v.x)
                xmax = v.x;
            if (ymin > v.y)
                ymin = v.y;
            else if (ymax < v.y)
                ymax = v.y;
            if (zmin > v.z)
                zmin = v.z;

            side.x = -1f;
            side.y = 1f;
            v = side.transform(om);
            if (xmin > v.x)
                xmin = v.x;
            else if (xmax < v.x)
                xmax = v.x;
            if (ymin > v.y)
                ymin = v.y;
            else if (ymax < v.y)
                ymax = v.y;
            if (zmin > v.z)
                zmin = v.z;

            side.x = 1f;
            v = side.transform(om);
            if (xmin > v.x)
                xmin = v.x;
            else if (xmax < v.x)
                xmax = v.x;
            if (ymin > v.y)
                ymin = v.y;
            else if (ymax < v.y)
                ymax = v.y;
            if (zmin > v.z)
                zmin = v.z;

            side.x = -1f;
            side.y = -1f;
            side.z = 1f;
            v = side.transform(om);
            if (xmin > v.x)
                xmin = v.x;
            else if (xmax < v.x)
                xmax = v.x;
            if (ymin > v.y)
                ymin = v.y;
            else if (ymax < v.y)
                ymax = v.y;
            if (zmin > v.z)
                zmin = v.z;

            side.x = 1f;
            v = side.transform(om);
            if (xmin > v.x)
                xmin = v.x;
            else if (xmax < v.x)
                xmax = v.x;
            if (ymin > v.y)
                ymin = v.y;
            else if (ymax < v.y)
                ymax = v.y;
            if (zmin > v.z)
                zmin = v.z;

            side.x = -1f;
            side.y = 1f;
            v = side.transform(om);
            if (xmin > v.x)
                xmin = v.x;
            else if (xmax < v.x)
                xmax = v.x;
            if (ymin > v.y)
                ymin = v.y;
            else if (ymax < v.y)
                ymax = v.y;
            if (zmin > v.z)
                zmin = v.z;

            side.x = 1f;
            v = side.transform(om);
            if (xmin > v.x)
                xmin = v.x;
            else if (xmax < v.x)
                xmax = v.x;
            if (ymin > v.y)
                ymin = v.y;
            else if (ymax < v.y)
                ymax = v.y;
            if (zmin > v.z)
                zmin = v.z;


            xmax -= xmin;
            ymax -= ymin;

            if (xmax < 1f || ymax < 1f)
                return -1;
            return xmax * ymax / (width * height);
        }
    }
}
