/*
 * Copyright (c) Contributors, http://opensimulator.org/
 * See CONTRIBUTORS.TXT for a full list of copyright holders.
 *
 * Redistribution and use in source and binary forms, with or without
 * modification, are permitted provided that the following conditions are met:
 *     * Redistributions of source code must retain the above copyright
 *       notice, this list of conditions and the following disclaimer.
 *     * Redistributions in binary form must reproduce the above copyright
 *       notice, this list of conditions and the following disclaimer in the
 *       documentation and/or other materials provided with the distribution.
 *     * Neither the name of the OpenSimulator Project nor the
 *       names of its contributors may be used to endorse or promote products
 *       derived from this software without specific prior written permission.
 *
 * THIS SOFTWARE IS PROVIDED BY THE DEVELOPERS ``AS IS'' AND ANY
 * EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
 * WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
 * DISCLAIMED. IN NO EVENT SHALL THE CONTRIBUTORS BE LIABLE FOR ANY
 * DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
 * (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
 * LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND
 * ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
 * (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
 * SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
 */

using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Text;
using LitJson;
using OpenMetaverse;
using OpenMetaverse.Rendering;
using OpenSim.Framework;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Region.ScriptEngine.Interfaces;
using OpenSim.Region.ScriptEngine.Shared.Api.Interfaces;
using OpenSim.Region.ScriptEngine.Shared.ScriptBase;
using LSL_Float = OpenSim.Region.ScriptEngine.Shared.LSL_Types.LSLFloat;
using LSL_Integer = OpenSim.Region.ScriptEngine.Shared.LSL_Types.LSLInteger;
using LSL_Key = OpenSim.Region.ScriptEngine.Shared.LSL_Types.LSLString;
using LSL_List = OpenSim.Region.ScriptEngine.Shared.LSL_Types.list;
using LSL_Rotation = OpenSim.Region.ScriptEngine.Shared.LSL_Types.Quaternion;
using LSL_String = OpenSim.Region.ScriptEngine.Shared.LSL_Types.LSLString;
using LSL_Vector = OpenSim.Region.ScriptEngine.Shared.LSL_Types.Vector3;

namespace OpenSim.Region.ScriptEngine.Shared.Api
{
    public partial class LSL_Api : MarshalByRefObject, ILSL_Api, IScriptApi
    {
        /// <summary>
        ///     Helper to check if a ray intersects a shape bounding box.
        /// </summary>
        private bool RayIntersectsShapeBox(Vector3 pos1RayProj, Vector3 pos2RayProj, Vector3 shapeBoxMax)
        {
            // Skip if ray can't intersect bounding box;
            var rayBoxProjMin = Vector3.Min(pos1RayProj, pos2RayProj);
            var rayBoxProjMax = Vector3.Max(pos1RayProj, pos2RayProj);
            if (
                rayBoxProjMin.X > shapeBoxMax.X || rayBoxProjMin.Y > shapeBoxMax.Y || rayBoxProjMin.Z > shapeBoxMax.Z ||
                rayBoxProjMax.X < -shapeBoxMax.X || rayBoxProjMax.Y < -shapeBoxMax.Y || rayBoxProjMax.Z < -shapeBoxMax.Z
            )
                return false;

            // Check if ray intersect any bounding box side
            var sign = 0;
            var dist = 0.0f;
            var posProj = Vector3.Zero;
            var vecRayProj = pos2RayProj - pos1RayProj;

            // Check both X sides unless ray is parallell to them
            if (Math.Abs(vecRayProj.X) > m_floatToleranceInCastRay)
                for (sign = -1; sign <= 1; sign += 2)
                {
                    dist = (sign * shapeBoxMax.X - pos1RayProj.X) / vecRayProj.X;
                    posProj = pos1RayProj + vecRayProj * dist;
                    if (Math.Abs(posProj.Y) <= shapeBoxMax.Y && Math.Abs(posProj.Z) <= shapeBoxMax.Z)
                        return true;
                }

            // Check both Y sides unless ray is parallell to them
            if (Math.Abs(vecRayProj.Y) > m_floatToleranceInCastRay)
                for (sign = -1; sign <= 1; sign += 2)
                {
                    dist = (sign * shapeBoxMax.Y - pos1RayProj.Y) / vecRayProj.Y;
                    posProj = pos1RayProj + vecRayProj * dist;
                    if (Math.Abs(posProj.X) <= shapeBoxMax.X && Math.Abs(posProj.Z) <= shapeBoxMax.Z)
                        return true;
                }

            // Check both Z sides unless ray is parallell to them
            if (Math.Abs(vecRayProj.Z) > m_floatToleranceInCastRay)
                for (sign = -1; sign <= 1; sign += 2)
                {
                    dist = (sign * shapeBoxMax.Z - pos1RayProj.Z) / vecRayProj.Z;
                    posProj = pos1RayProj + vecRayProj * dist;
                    if (Math.Abs(posProj.X) <= shapeBoxMax.X && Math.Abs(posProj.Y) <= shapeBoxMax.Y)
                        return true;
                }

            // No hits on bounding box so return false
            return false;
        }

        /// <summary>
        ///     Helper to parse FacetedMesh for ray hits.
        /// </summary>
        private void AddRayInFacetedMesh(FacetedMesh mesh, RayTrans rayTrans, ref List<RayHit> rayHits)
        {
            if (mesh != null)
                foreach (var face in mesh.Faces)
                    for (var i = 0; i < face.Indices.Count; i += 3)
                    {
                        var triangle = new Tri
                        {
                            p1 = face.Vertices[face.Indices[i]].Position,
                            p2 = face.Vertices[face.Indices[i + 1]].Position,
                            p3 = face.Vertices[face.Indices[i + 2]].Position
                        };
                        AddRayInTri(triangle, rayTrans, ref rayHits);
                    }
        }

        /// <summary>
        ///     Helper to parse Tri (triangle) List for ray hits.
        /// </summary>
        private void AddRayInTris(List<Tri> triangles, RayTrans rayTrans, ref List<RayHit> rayHits)
        {
            foreach (var triangle in triangles) AddRayInTri(triangle, rayTrans, ref rayHits);
        }

        /// <summary>
        ///     Helper to add ray hit in a Tri (triangle).
        /// </summary>
        private void AddRayInTri(Tri triProj, RayTrans rayTrans, ref List<RayHit> rayHits)
        {
            // Check for hit in triangle
            if (HitRayInTri(triProj, rayTrans.Position1RayProj, rayTrans.VectorRayProj, out var posHitProj,
                    out var normalProj))
            {
                // Hack to circumvent ghost face bug in PrimMesher by removing hits in (ghost) face plane through shape center
                if (Math.Abs(Vector3.Dot(posHitProj, normalProj)) < m_floatToleranceInCastRay &&
                    !rayTrans.ShapeNeedsEnds)
                    return;

                // Transform hit and normal to region coordinate system
                var posHit = rayTrans.PositionPart + posHitProj * rayTrans.ScalePart * rayTrans.RotationPart;
                var normal = Vector3.Normalize(normalProj * rayTrans.ScalePart * rayTrans.RotationPart);

                // Remove duplicate hits at triangle intersections
                var distance = Vector3.Distance(rayTrans.Position1Ray, posHit);
                for (var i = rayHits.Count - 1; i >= 0; i--)
                {
                    if (rayHits[i].PartId != rayTrans.PartId)
                        break;
                    if (Math.Abs(rayHits[i].Distance - distance) < m_floatTolerance2InCastRay)
                        return;
                }

                // Build result data set
                var rayHit = new RayHit
                {
                    PartId = rayTrans.PartId,
                    GroupId = rayTrans.GroupId,
                    Link = rayTrans.Link,
                    Position = posHit,
                    Normal = normal,
                    Distance = distance
                };
                rayHits.Add(rayHit);
            }
        }

        /// <summary>
        ///     Helper to find ray hit in triangle
        /// </summary>
        private bool HitRayInTri(Tri triProj, Vector3 pos1RayProj, Vector3 vecRayProj, out Vector3 posHitProj,
            out Vector3 normalProj)
        {
            var tol = m_floatToleranceInCastRay;
            posHitProj = Vector3.Zero;

            // Calculate triangle edge vectors
            var vec1Proj = triProj.p2 - triProj.p1;
            var vec2Proj = triProj.p3 - triProj.p2;
            var vec3Proj = triProj.p1 - triProj.p3;

            // Calculate triangle normal
            normalProj = Vector3.Cross(vec1Proj, vec2Proj);

            // Skip if degenerate triangle or ray parallell with triangle plane
            var divisor = Vector3.Dot(vecRayProj, normalProj);
            if (Math.Abs(divisor) < tol)
                return false;

            // Skip if exit and not configured to detect
            if (divisor > tol && !m_detectExitsInCastRay)
                return false;

            // Skip if outside ray ends
            var distanceProj = Vector3.Dot(triProj.p1 - pos1RayProj, normalProj) / divisor;
            if (distanceProj < -tol || distanceProj > 1 + tol)
                return false;

            // Calculate hit position in triangle
            posHitProj = pos1RayProj + vecRayProj * distanceProj;

            // Skip if outside triangle bounding box
            var triProjMin = Vector3.Min(Vector3.Min(triProj.p1, triProj.p2), triProj.p3);
            var triProjMax = Vector3.Max(Vector3.Max(triProj.p1, triProj.p2), triProj.p3);
            if (
                posHitProj.X < triProjMin.X - tol || posHitProj.Y < triProjMin.Y - tol ||
                posHitProj.Z < triProjMin.Z - tol ||
                posHitProj.X > triProjMax.X + tol || posHitProj.Y > triProjMax.Y + tol ||
                posHitProj.Z > triProjMax.Z + tol
            )
                return false;

            // Skip if outside triangle
            if (
                Vector3.Dot(Vector3.Cross(vec1Proj, normalProj), posHitProj - triProj.p1) > tol ||
                Vector3.Dot(Vector3.Cross(vec2Proj, normalProj), posHitProj - triProj.p2) > tol ||
                Vector3.Dot(Vector3.Cross(vec3Proj, normalProj), posHitProj - triProj.p3) > tol
            )
                return false;

            // Return hit
            return true;
        }

        /// <summary>
        ///     Helper to parse selected parts of HeightMap into a Tri (triangle) List and calculate bounding box.
        /// </summary>
        private List<Tri> TrisFromHeightmapUnderRay(Vector3 posStart, Vector3 posEnd, out Vector3 lower,
            out Vector3 upper)
        {
            // Get bounding X-Y rectangle of terrain under ray
            lower = Vector3.Min(posStart, posEnd);
            upper = Vector3.Max(posStart, posEnd);
            lower.X = (float)Math.Floor(lower.X);
            lower.Y = (float)Math.Floor(lower.Y);
            var zLower = float.MaxValue;
            upper.X = (float)Math.Ceiling(upper.X);
            upper.Y = (float)Math.Ceiling(upper.Y);
            var zUpper = float.MinValue;

            // Initialize Tri (triangle) List
            var triangles = new List<Tri>();

            // Set parsing lane direction to major ray X-Y axis
            var vec = posEnd - posStart;
            var xAbs = Math.Abs(vec.X);
            var yAbs = Math.Abs(vec.Y);
            var bigX = true;
            if (yAbs > xAbs)
            {
                bigX = false;
                vec = vec / yAbs;
            }
            else if (xAbs > yAbs || xAbs > 0.0f)
            {
                vec = vec / xAbs;
            }
            else
            {
                vec = new Vector3(1.0f, 1.0f, 0.0f);
            }

            // Simplify by start parsing in lower end of lane
            if ((bigX && vec.X < 0.0f) || (!bigX && vec.Y < 0.0f))
            {
                var posTemp = posStart;
                posStart = posEnd;
                posEnd = posTemp;
                vec = vec * -1.0f;
            }

            // First 1x1 rectangle under ray
            var xFloorOld = 0.0f;
            var yFloorOld = 0.0f;
            var pos = posStart;
            var xFloor = (float)Math.Floor(pos.X);
            var yFloor = (float)Math.Floor(pos.Y);
            AddTrisFromHeightmap(xFloor, yFloor, ref triangles, ref zLower, ref zUpper);

            // Parse every remaining 1x1 rectangle under ray
            while (pos != posEnd)
            {
                // Next 1x1 rectangle under ray
                xFloorOld = xFloor;
                yFloorOld = yFloor;
                pos = pos + vec;

                // Clip position to 1x1 rectangle border
                xFloor = (float)Math.Floor(pos.X);
                yFloor = (float)Math.Floor(pos.Y);
                if (bigX && pos.X > xFloor)
                {
                    pos.Y -= vec.Y * (pos.X - xFloor);
                    pos.X = xFloor;
                }
                else if (!bigX && pos.Y > yFloor)
                {
                    pos.X -= vec.X * (pos.Y - yFloor);
                    pos.Y = yFloor;
                }

                // Last 1x1 rectangle under ray
                if ((bigX && pos.X >= posEnd.X) || (!bigX && pos.Y >= posEnd.Y))
                {
                    pos = posEnd;
                    xFloor = (float)Math.Floor(pos.X);
                    yFloor = (float)Math.Floor(pos.Y);
                }

                // Add new 1x1 rectangle in lane
                if ((bigX && xFloor != xFloorOld) || (!bigX && yFloor != yFloorOld))
                    AddTrisFromHeightmap(xFloor, yFloor, ref triangles, ref zLower, ref zUpper);
                // Add last 1x1 rectangle in old lane at lane shift
                if (bigX && yFloor != yFloorOld)
                    AddTrisFromHeightmap(xFloor, yFloorOld, ref triangles, ref zLower, ref zUpper);
                if (!bigX && xFloor != xFloorOld)
                    AddTrisFromHeightmap(xFloorOld, yFloor, ref triangles, ref zLower, ref zUpper);
            }

            // Finalize bounding box Z
            lower.Z = zLower;
            upper.Z = zUpper;

            // Done and returning Tri (triangle)List
            return triangles;
        }

        /// <summary>
        ///     Helper to add HeightMap squares into Tri (triangle) List and adjust bounding box.
        /// </summary>
        private void AddTrisFromHeightmap(float xPos, float yPos, ref List<Tri> triangles, ref float zLower,
            ref float zUpper)
        {
            var xInt = (int)xPos;
            var yInt = (int)yPos;

            // Corner 1 of 1x1 rectangle
            var x = Util.Clamp(xInt + 1, 0, World.Heightmap.Width - 1);
            var y = Util.Clamp(yInt + 1, 0, World.Heightmap.Height - 1);
            var pos1 = new Vector3(x, y, World.Heightmap[x, y]);
            // Adjust bounding box
            zLower = Math.Min(zLower, pos1.Z);
            zUpper = Math.Max(zUpper, pos1.Z);

            // Corner 2 of 1x1 rectangle
            x = Util.Clamp(xInt, 0, World.Heightmap.Width - 1);
            y = Util.Clamp(yInt + 1, 0, World.Heightmap.Height - 1);
            var pos2 = new Vector3(x, y, World.Heightmap[x, y]);
            // Adjust bounding box
            zLower = Math.Min(zLower, pos2.Z);
            zUpper = Math.Max(zUpper, pos2.Z);

            // Corner 3 of 1x1 rectangle
            x = Util.Clamp(xInt, 0, World.Heightmap.Width - 1);
            y = Util.Clamp(yInt, 0, World.Heightmap.Height - 1);
            var pos3 = new Vector3(x, y, World.Heightmap[x, y]);
            // Adjust bounding box
            zLower = Math.Min(zLower, pos3.Z);
            zUpper = Math.Max(zUpper, pos3.Z);

            // Corner 4 of 1x1 rectangle
            x = Util.Clamp(xInt + 1, 0, World.Heightmap.Width - 1);
            y = Util.Clamp(yInt, 0, World.Heightmap.Height - 1);
            var pos4 = new Vector3(x, y, World.Heightmap[x, y]);
            // Adjust bounding box
            zLower = Math.Min(zLower, pos4.Z);
            zUpper = Math.Max(zUpper, pos4.Z);

            // Add triangle 1
            var triangle1 = new Tri
            {
                p1 = pos1,
                p2 = pos2,
                p3 = pos3
            };
            triangles.Add(triangle1);

            // Add triangle 2
            var triangle2 = new Tri
            {
                p1 = pos3,
                p2 = pos4,
                p3 = pos1
            };
            triangles.Add(triangle2);
        }

        /// <summary>
        ///     Helper to get link number for a UUID.
        /// </summary>
        private int UUID2LinkNumber(SceneObjectPart part, UUID id)
        {
            var group = part.ParentGroup;
            if (group != null)
            {
                // Parse every link for UUID
                var linkCount = group.PrimCount + group.GetSittingAvatarsCount();
                for (var link = linkCount; link > 0; link--)
                {
                    var entity = GetLinkEntity(part, link);
                    // Return link number if UUID match
                    if (entity != null && entity.UUID == id)
                        return link;
                }
            }

            // Return link number 0 if no links or UUID matches
            return 0;
        }


        protected LSL_List SetPrimParams(ScenePresence av, LSL_List rules, string originFunc, ref uint rulesParsed)
        {
            //This is a special version of SetPrimParams to deal with avatars which are sitting on the linkset.

            var idx = 0;
            var idxStart = 0;

            var positionChanged = false;
            try
            {
                while (idx < rules.Length)
                {
                    ++rulesParsed;
                    int code = rules.GetLSLIntegerItem(idx++);

                    var remain = rules.Length - idx;
                    idxStart = idx;

                    switch (code)
                    {
                        case ScriptBaseClass.PRIM_POSITION:
                        case ScriptBaseClass.PRIM_POS_LOCAL:
                        {
                            if (remain < 1)
                                return new LSL_List();

                            LSL_Vector v;
                            v = rules.GetVector3Item(idx++);

                            if (!av.LegacySitOffsets)
                            {
                                var sitOffset =
                                    llRot2Up(new LSL_Rotation(av.Rotation.X, av.Rotation.Y, av.Rotation.Z,
                                        av.Rotation.W)) * av.Appearance.AvatarHeight * 0.02638f;

                                v = v + 2 * sitOffset;
                            }

                            av.OffsetPosition = new Vector3((float)v.x, (float)v.y, (float)v.z);
                            positionChanged = true;
                        }
                            break;

                        case ScriptBaseClass.PRIM_ROTATION:
                        {
                            if (remain < 1)
                                return new LSL_List();

                            Quaternion r;
                            r = rules.GetQuaternionItem(idx++);

                            av.Rotation = m_host.GetWorldRotation() * r;
                            positionChanged = true;
                        }
                            break;

                        case ScriptBaseClass.PRIM_ROT_LOCAL:
                        {
                            if (remain < 1)
                                return new LSL_List();

                            LSL_Rotation r;
                            r = rules.GetQuaternionItem(idx++);

                            av.Rotation = r;
                            positionChanged = true;
                        }
                            break;

                        // parse rest doing nothing but number of parameters error check
                        case ScriptBaseClass.PRIM_SIZE:
                        case ScriptBaseClass.PRIM_MATERIAL:
                        case ScriptBaseClass.PRIM_PHANTOM:
                        case ScriptBaseClass.PRIM_PHYSICS:
                        case ScriptBaseClass.PRIM_PHYSICS_SHAPE_TYPE:
                        case ScriptBaseClass.PRIM_TEMP_ON_REZ:
                        case ScriptBaseClass.PRIM_NAME:
                        case ScriptBaseClass.PRIM_DESC:
                            if (remain < 1)
                                return new LSL_List();
                            idx++;
                            break;

                        case ScriptBaseClass.PRIM_GLOW:
                        case ScriptBaseClass.PRIM_FULLBRIGHT:
                        case ScriptBaseClass.PRIM_TEXGEN:
                            if (remain < 2)
                                return new LSL_List();
                            idx += 2;
                            break;

                        case ScriptBaseClass.PRIM_TYPE:
                            if (remain < 3)
                                return new LSL_List();
                            code = rules.GetLSLIntegerItem(idx++);
                            remain = rules.Length - idx;
                            switch (code)
                            {
                                case ScriptBaseClass.PRIM_TYPE_BOX:
                                case ScriptBaseClass.PRIM_TYPE_CYLINDER:
                                case ScriptBaseClass.PRIM_TYPE_PRISM:
                                    if (remain < 6)
                                        return new LSL_List();
                                    idx += 6;
                                    break;

                                case ScriptBaseClass.PRIM_TYPE_SPHERE:
                                    if (remain < 5)
                                        return new LSL_List();
                                    idx += 5;
                                    break;

                                case ScriptBaseClass.PRIM_TYPE_TORUS:
                                case ScriptBaseClass.PRIM_TYPE_TUBE:
                                case ScriptBaseClass.PRIM_TYPE_RING:
                                    if (remain < 11)
                                        return new LSL_List();
                                    idx += 11;
                                    break;

                                case ScriptBaseClass.PRIM_TYPE_SCULPT:
                                    if (remain < 2)
                                        return new LSL_List();
                                    idx += 2;
                                    break;
                            }

                            break;

                        case ScriptBaseClass.PRIM_COLOR:
                        case ScriptBaseClass.PRIM_TEXT:
                        case ScriptBaseClass.PRIM_BUMP_SHINY:
                        case ScriptBaseClass.PRIM_OMEGA:
                        case ScriptBaseClass.PRIM_SIT_TARGET:
                            if (remain < 3)
                                return new LSL_List();
                            idx += 3;
                            break;

                        case ScriptBaseClass.PRIM_TEXTURE:
                        case ScriptBaseClass.PRIM_POINT_LIGHT:
                        case ScriptBaseClass.PRIM_PHYSICS_MATERIAL:
                            if (remain < 5)
                                return new LSL_List();
                            idx += 5;
                            break;

                        case ScriptBaseClass.PRIM_FLEXIBLE:
                            if (remain < 7)
                                return new LSL_List();

                            idx += 7;
                            break;

                        case ScriptBaseClass.PRIM_LINK_TARGET:
                            if (remain <
                                3) // setting to 3 on the basis that parsing any usage of PRIM_LINK_TARGET that has nothing following it is pointless.
                                return new LSL_List();

                            return rules.GetSublist(idx, -1);
                    }
                }
            }
            catch (InvalidCastException e)
            {
                Error(originFunc, string.Format(
                    " error running rule #{0}: arg #{1} {2}",
                    rulesParsed, idx - idxStart, e.Message));
            }
            finally
            {
                if (positionChanged)
                    av.SendTerseUpdateToAllClients();
            }

            return new LSL_List();
        }

        public LSL_List GetPrimParams(ScenePresence avatar, LSL_List rules, ref LSL_List res)
        {
            // avatars case
            // replies as SL wiki

//            SceneObjectPart sitPart = avatar.ParentPart; // most likelly it will be needed
            var
                sitPart = World.GetSceneObjectPart(avatar
                    .ParentID); // maybe better do this expensive search for it in case it's gone??

            var idx = 0;
            while (idx < rules.Length)
            {
                int code = rules.GetLSLIntegerItem(idx++);
                var remain = rules.Length - idx;

                switch (code)
                {
                    case ScriptBaseClass.PRIM_MATERIAL:
                        res.Add(new LSL_Integer((int)SOPMaterialData.SopMaterial.Flesh));
                        break;

                    case ScriptBaseClass.PRIM_PHYSICS:
                        res.Add(new LSL_Integer(0));
                        break;

                    case ScriptBaseClass.PRIM_TEMP_ON_REZ:
                        res.Add(new LSL_Integer(0));
                        break;

                    case ScriptBaseClass.PRIM_PHANTOM:
                        res.Add(new LSL_Integer(0));
                        break;

                    case ScriptBaseClass.PRIM_POSITION:
                        Vector3 pos;

                        if (sitPart.ParentGroup.RootPart != null)
                        {
                            pos = avatar.OffsetPosition;

                            if (!avatar.LegacySitOffsets)
                            {
                                var sitOffset = Zrot(avatar.Rotation) *
                                                (avatar.Appearance.AvatarHeight * 0.02638f * 2.0f);
                                pos -= sitOffset;
                            }

                            var sitroot = sitPart.ParentGroup.RootPart;
                            pos = sitroot.AbsolutePosition + pos * sitroot.GetWorldRotation();
                        }
                        else
                        {
                            pos = avatar.AbsolutePosition;
                        }

                        res.Add(new LSL_Vector(pos.X, pos.Y, pos.Z));
                        break;

                    case ScriptBaseClass.PRIM_SIZE:
                        var s = avatar.Appearance.AvatarSize;
                        res.Add(new LSL_Vector(s.X, s.Y, s.Z));

                        break;

                    case ScriptBaseClass.PRIM_ROTATION:
                        res.Add(new LSL_Rotation(avatar.GetWorldRotation()));
                        break;

                    case ScriptBaseClass.PRIM_TYPE:
                        res.Add(new LSL_Integer(ScriptBaseClass.PRIM_TYPE_BOX));
                        res.Add(new LSL_Integer(ScriptBaseClass.PRIM_HOLE_DEFAULT));
                        res.Add(new LSL_Vector(0f, 1.0f, 0f));
                        res.Add(new LSL_Float(0.0f));
                        res.Add(new LSL_Vector(0, 0, 0));
                        res.Add(new LSL_Vector(1.0f, 1.0f, 0f));
                        res.Add(new LSL_Vector(0, 0, 0));
                        break;

                    case ScriptBaseClass.PRIM_TEXTURE:
                        if (remain < 1)
                            return new LSL_List();

                        int face = rules.GetLSLIntegerItem(idx++);
                        if (face == ScriptBaseClass.ALL_SIDES)
                        {
                            for (face = 0; face < 21; face++)
                            {
                                res.Add(new LSL_String(""));
                                res.Add(new LSL_Vector(0, 0, 0));
                                res.Add(new LSL_Vector(0, 0, 0));
                                res.Add(new LSL_Float(0.0));
                            }
                        }
                        else
                        {
                            if (face >= 0 && face < 21)
                            {
                                res.Add(new LSL_String(""));
                                res.Add(new LSL_Vector(0, 0, 0));
                                res.Add(new LSL_Vector(0, 0, 0));
                                res.Add(new LSL_Float(0.0));
                            }
                        }

                        break;

                    case ScriptBaseClass.PRIM_COLOR:
                        if (remain < 1)
                            return new LSL_List();

                        face = rules.GetLSLIntegerItem(idx++);

                        if (face == ScriptBaseClass.ALL_SIDES)
                        {
                            for (face = 0; face < 21; face++)
                            {
                                res.Add(new LSL_Vector(0, 0, 0));
                                res.Add(new LSL_Float(0));
                            }
                        }
                        else
                        {
                            res.Add(new LSL_Vector(0, 0, 0));
                            res.Add(new LSL_Float(0));
                        }

                        break;

                    case ScriptBaseClass.PRIM_BUMP_SHINY:
                        if (remain < 1)
                            return new LSL_List();
                        face = rules.GetLSLIntegerItem(idx++);

                        if (face == ScriptBaseClass.ALL_SIDES)
                        {
                            for (face = 0; face < 21; face++)
                            {
                                res.Add(new LSL_Integer(ScriptBaseClass.PRIM_SHINY_NONE));
                                res.Add(new LSL_Integer(ScriptBaseClass.PRIM_BUMP_NONE));
                            }
                        }
                        else
                        {
                            res.Add(new LSL_Integer(ScriptBaseClass.PRIM_SHINY_NONE));
                            res.Add(new LSL_Integer(ScriptBaseClass.PRIM_BUMP_NONE));
                        }

                        break;

                    case ScriptBaseClass.PRIM_FULLBRIGHT:
                        if (remain < 1)
                            return new LSL_List();
                        face = rules.GetLSLIntegerItem(idx++);

                        if (face == ScriptBaseClass.ALL_SIDES)
                            for (face = 0; face < 21; face++)
                                res.Add(new LSL_Integer(ScriptBaseClass.FALSE));
                        else
                            res.Add(new LSL_Integer(ScriptBaseClass.FALSE));
                        break;

                    case ScriptBaseClass.PRIM_FLEXIBLE:
                        res.Add(new LSL_Integer(0));
                        res.Add(new LSL_Integer(0)); // softness
                        res.Add(new LSL_Float(0.0f)); // gravity
                        res.Add(new LSL_Float(0.0f)); // friction
                        res.Add(new LSL_Float(0.0f)); // wind
                        res.Add(new LSL_Float(0.0f)); // tension
                        res.Add(new LSL_Vector(0f, 0f, 0f));
                        break;

                    case ScriptBaseClass.PRIM_TEXGEN:
                        // (PRIM_TEXGEN_DEFAULT, PRIM_TEXGEN_PLANAR)
                        if (remain < 1)
                            return new LSL_List();
                        face = rules.GetLSLIntegerItem(idx++);

                        if (face == ScriptBaseClass.ALL_SIDES)
                            for (face = 0; face < 21; face++)
                                res.Add(new LSL_Integer(ScriptBaseClass.PRIM_TEXGEN_DEFAULT));
                        else
                            res.Add(new LSL_Integer(ScriptBaseClass.PRIM_TEXGEN_DEFAULT));
                        break;

                    case ScriptBaseClass.PRIM_POINT_LIGHT:
                        res.Add(new LSL_Integer(0));
                        res.Add(new LSL_Vector(0f, 0f, 0f));
                        res.Add(new LSL_Float(0f)); // intensity
                        res.Add(new LSL_Float(0f)); // radius
                        res.Add(new LSL_Float(0f)); // falloff
                        break;

                    case ScriptBaseClass.PRIM_GLOW:
                        if (remain < 1)
                            return new LSL_List();
                        face = rules.GetLSLIntegerItem(idx++);

                        if (face == ScriptBaseClass.ALL_SIDES)
                            for (face = 0; face < 21; face++)
                                res.Add(new LSL_Float(0f));
                        else
                            res.Add(new LSL_Float(0f));
                        break;

                    case ScriptBaseClass.PRIM_TEXT:
                        res.Add(new LSL_String(""));
                        res.Add(new LSL_Vector(0f, 0f, 0f));
                        res.Add(new LSL_Float(1.0f));
                        break;

                    case ScriptBaseClass.PRIM_NAME:
                        res.Add(new LSL_String(avatar.Name));
                        break;

                    case ScriptBaseClass.PRIM_DESC:
                        res.Add(new LSL_String(""));
                        break;

                    case ScriptBaseClass.PRIM_ROT_LOCAL:
                        var lrot = avatar.Rotation;
                        res.Add(new LSL_Rotation(lrot.X, lrot.Y, lrot.Z, lrot.W));
                        break;

                    case ScriptBaseClass.PRIM_POS_LOCAL:
                        var lpos = avatar.OffsetPosition;

                        if (!avatar.LegacySitOffsets)
                        {
                            var lsitOffset = Zrot(avatar.Rotation) * (avatar.Appearance.AvatarHeight * 0.02638f * 2.0f);
                            lpos -= lsitOffset;
                        }

                        res.Add(new LSL_Vector(lpos.X, lpos.Y, lpos.Z));
                        break;

                    case ScriptBaseClass.PRIM_LINK_TARGET:
                        if (remain < 3) // setting to 3 on the basis that parsing any usage of PRIM_LINK_TARGET that has nothing following it is pointless.
                            return new LSL_List();

                        return rules.GetSublist(idx, -1);
                }
            }

            return new LSL_List();
        }

        private LSL_List JsonParseTop(JsonData elem)
        {
            var retl = new LSL_List();
            if (elem == null)
                retl.Add((LSL_String)ScriptBaseClass.JSON_NULL);

            var elemType = elem.GetJsonType();
            switch (elemType)
            {
                case JsonType.Int:
                    retl.Add(new LSL_Integer((int)elem));
                    return retl;
                case JsonType.Boolean:
                    retl.Add((LSL_String)((bool)elem ? ScriptBaseClass.JSON_TRUE : ScriptBaseClass.JSON_FALSE));
                    return retl;
                case JsonType.Double:
                    retl.Add(new LSL_Float((double)elem));
                    return retl;
                case JsonType.None:
                    retl.Add((LSL_String)ScriptBaseClass.JSON_NULL);
                    return retl;
                case JsonType.String:
                    retl.Add(new LSL_String((string)elem));
                    return retl;
                case JsonType.Array:
                    foreach (JsonData subelem in elem)
                        retl.Add(JsonParseTopNodes(subelem));
                    return retl;
                case JsonType.Object:
                    var e = ((IOrderedDictionary)elem).GetEnumerator();
                    while (e.MoveNext())
                    {
                        retl.Add(new LSL_String((string)e.Key));
                        retl.Add(JsonParseTopNodes((JsonData)e.Value));
                    }

                    return retl;
                default:
                    throw new Exception(ScriptBaseClass.JSON_INVALID);
            }
        }

        private object JsonParseTopNodes(JsonData elem)
        {
            if (elem == null)
                return (LSL_String)ScriptBaseClass.JSON_NULL;

            var elemType = elem.GetJsonType();
            switch (elemType)
            {
                case JsonType.Int:
                    return new LSL_Integer((int)elem);
                case JsonType.Boolean:
                    return (bool)elem ? (LSL_String)ScriptBaseClass.JSON_TRUE : (LSL_String)ScriptBaseClass.JSON_FALSE;
                case JsonType.Double:
                    return new LSL_Float((double)elem);
                case JsonType.None:
                    return (LSL_String)ScriptBaseClass.JSON_NULL;
                case JsonType.String:
                    return new LSL_String((string)elem);
                case JsonType.Array:
                case JsonType.Object:
                    var s = JsonMapper.ToJson(elem);
                    return (LSL_String)s;
                default:
                    throw new Exception(ScriptBaseClass.JSON_INVALID);
            }
        }

        private string ListToJson(object o)
        {
            if (o is LSL_Float || o is double)
            {
                double float_val;
                if (o is double)
                    float_val = (double)o;
                else
                    float_val = ((LSL_Float)o).value;

                if (double.IsInfinity(float_val))
                    return "\"Inf\"";
                if (double.IsNaN(float_val))
                    return "\"NaN\"";

                return ((LSL_Float)float_val).ToString();
            }

            if (o is LSL_Integer || o is int)
            {
                int i;
                if (o is int)
                    i = (int)o;
                else
                    i = ((LSL_Integer)o).value;
                return i.ToString();
            }

            if (o is LSL_Rotation)
            {
                var sb = new StringBuilder(128);
                sb.Append("\"");
                var r = (LSL_Rotation)o;
                sb.Append(r.ToString());
                sb.Append("\"");
                return sb.ToString();
            }

            if (o is LSL_Vector)
            {
                var sb = new StringBuilder(128);
                sb.Append("\"");
                var v = (LSL_Vector)o;
                sb.Append(v.ToString());
                sb.Append("\"");
                return sb.ToString();
            }

            if (o is LSL_String || o is string)
            {
                string str;
                if (o is string)
                    str = (string)o;
                else
                    str = ((LSL_String)o).m_string;

                if (str == ScriptBaseClass.JSON_TRUE || str == "true")
                    return "true";
                if (str == ScriptBaseClass.JSON_FALSE || str == "false")
                    return "false";
                if (str == ScriptBaseClass.JSON_NULL || str == "null")
                    return "null";
                str.Trim();
                if (str.Length == 0)
                    return "\"\"";
                if (str[0] == '{')
                    return str;
                if (str[0] == '[')
                    return str;
                return EscapeForJSON(str, true);
            }

            throw new IndexOutOfRangeException();
        }

        private string EscapeForJSON(string s, bool AddOuter)
        {
            int i;
            char c;
            string t;
            var len = s.Length;

            var sb = new StringBuilder(len + 64);
            if (AddOuter)
                sb.Append("\"");

            for (i = 0; i < len; i++)
            {
                c = s[i];
                switch (c)
                {
                    case '\\':
                    case '"':
                    case '/':
                        sb.Append('\\');
                        sb.Append(c);
                        break;
                    case '\b':
                        sb.Append("\\b");
                        break;
                    case '\t':
                        sb.Append("\\t");
                        break;
                    case '\n':
                        sb.Append("\\n");
                        break;
                    case '\f':
                        sb.Append("\\f");
                        break;
                    case '\r':
                        sb.Append("\\r");
                        break;
                    default:
                        if (c < ' ')
                        {
                            t = "000" + string.Format("{0:X}", c);
                            sb.Append("\\u" + t.Substring(t.Length - 4));
                        }
                        else
                        {
                            sb.Append(c);
                        }

                        break;
                }
            }

            if (AddOuter)
                sb.Append("\"");
            return sb.ToString();
        }

        private JsonData JsonSetSpecific(JsonData elem, LSL_List specifiers, int level, LSL_String val)
        {
            var spec = specifiers.Data[level];
            if (spec is LSL_String)
                spec = ((LSL_String)spec).m_string;
            else if (spec is LSL_Integer)
                spec = ((LSL_Integer)spec).value;

            if (!(spec is string || spec is int))
                throw new IndexOutOfRangeException();

            var speclen = specifiers.Data.Length - 1;

            var hasvalue = false;
            JsonData value = null;

            var elemType = elem.GetJsonType();
            if (elemType == JsonType.Array)
            {
                if (spec is int)
                {
                    var v = (int)spec;
                    var c = elem.Count;
                    if (v < 0 || (v != 0 && v > c))
                        throw new IndexOutOfRangeException();
                    if (v == c)
                    {
                        elem.Add(JsonBuildRestOfSpec(specifiers, level + 1, val));
                    }
                    else
                    {
                        hasvalue = true;
                        value = elem[v];
                    }
                }
                else if (spec is string)
                {
                    if ((string)spec == ScriptBaseClass.JSON_APPEND)
                    {
                        elem.Add(JsonBuildRestOfSpec(specifiers, level + 1, val));
                    }
                    else if (elem.Count < 2)
                    {
                        // our initial guess of array was wrong
                        var newdata = new JsonData();
                        newdata.SetJsonType(JsonType.Object);
                        IOrderedDictionary no = newdata;
                        no.Add((string)spec, JsonBuildRestOfSpec(specifiers, level + 1, val));
                        return newdata;
                    }
                }
            }
            else if (elemType == JsonType.Object)
            {
                if (spec is string)
                {
                    IOrderedDictionary e = elem;
                    var key = (string)spec;
                    if (e.Contains(key))
                    {
                        hasvalue = true;
                        value = (JsonData)e[key];
                    }
                    else
                    {
                        e.Add(key, JsonBuildRestOfSpec(specifiers, level + 1, val));
                    }
                }
                else if (spec is int && (int)spec == 0)
                {
                    //we are replacing a object by a array
                    var newData = new JsonData();
                    newData.SetJsonType(JsonType.Array);
                    newData.Add(JsonBuildRestOfSpec(specifiers, level + 1, val));
                    return newData;
                }
            }
            else
            {
                var newData = JsonBuildRestOfSpec(specifiers, level, val);
                return newData;
            }

            if (hasvalue)
            {
                if (level < speclen)
                {
                    var replace = JsonSetSpecific(value, specifiers, level + 1, val);
                    if (replace != null)
                    {
                        if (elemType == JsonType.Array)
                        {
                            if (spec is int)
                            {
                                elem[(int)spec] = replace;
                            }
                            else if (spec is string)
                            {
                                var newdata = new JsonData();
                                newdata.SetJsonType(JsonType.Object);
                                IOrderedDictionary no = newdata;
                                no.Add((string)spec, replace);
                                return newdata;
                            }
                        }
                        else if (elemType == JsonType.Object)
                        {
                            if (spec is string)
                            {
                                elem[(string)spec] = replace;
                            }
                            else if (spec is int && (int)spec == 0)
                            {
                                var newdata = new JsonData();
                                newdata.SetJsonType(JsonType.Array);
                                newdata.Add(replace);
                                return newdata;
                            }
                        }
                    }

                    return null;
                }

                if (speclen == level)
                {
                    if (val == ScriptBaseClass.JSON_DELETE)
                    {
                        if (elemType == JsonType.Array)
                        {
                            if (spec is int)
                            {
                                IList el = elem;
                                el.RemoveAt((int)spec);
                            }
                        }
                        else if (elemType == JsonType.Object)
                        {
                            if (spec is string)
                            {
                                IOrderedDictionary eo = elem;
                                eo.Remove((string)spec);
                            }
                        }

                        return null;
                    }

                    JsonData newval = null;
                    if (val == null || val == ScriptBaseClass.JSON_NULL || val == "null")
                    {
                        newval = null;
                    }
                    else if (val == ScriptBaseClass.JSON_TRUE || val == "true")
                    {
                        newval = new JsonData(true);
                    }
                    else if (val == ScriptBaseClass.JSON_FALSE || val == "false")
                    {
                        newval = new JsonData(false);
                    }
                    else if (float.TryParse(val, out var num))
                    {
                        // assuming we are at en.us already
                        if (num - (int)num == 0.0f && !val.Contains("."))
                        {
                            newval = new JsonData((int)num);
                        }
                        else
                        {
                            num = (float)Math.Round(num, 6);
                            newval = new JsonData(num);
                        }
                    }
                    else
                    {
                        var str = val.m_string;
                        newval = new JsonData(str);
                    }

                    if (elemType == JsonType.Array)
                    {
                        if (spec is int)
                        {
                            elem[(int)spec] = newval;
                        }
                        else if (spec is string)
                        {
                            var newdata = new JsonData();
                            newdata.SetJsonType(JsonType.Object);
                            IOrderedDictionary no = newdata;
                            no.Add((string)spec, newval);
                            return newdata;
                        }
                    }
                    else if (elemType == JsonType.Object)
                    {
                        if (spec is string)
                        {
                            elem[(string)spec] = newval;
                        }
                        else if (spec is int && (int)spec == 0)
                        {
                            var newdata = new JsonData();
                            newdata.SetJsonType(JsonType.Array);
                            newdata.Add(newval);
                            return newdata;
                        }
                    }
                }
            }

            if (val == ScriptBaseClass.JSON_DELETE)
                throw new IndexOutOfRangeException();
            return null;
        }

        private JsonData JsonBuildRestOfSpec(LSL_List specifiers, int level, LSL_String val)
        {
            var spec = level >= specifiers.Data.Length ? null : specifiers.Data[level];
            // 20131224 not used            object specNext = i+1 >= specifiers.Data.Length ? null : specifiers.Data[i+1];

            if (spec == null)
            {
                if (val == null || val == ScriptBaseClass.JSON_NULL || val == "null")
                    return null;
                if (val == ScriptBaseClass.JSON_DELETE)
                    throw new IndexOutOfRangeException();
                if (val == ScriptBaseClass.JSON_TRUE || val == "true")
                    return new JsonData(true);
                if (val == ScriptBaseClass.JSON_FALSE || val == "false")
                    return new JsonData(false);
                if (val == null || val == ScriptBaseClass.JSON_NULL || val == "null")
                    return null;
                if (float.TryParse(val, out var num))
                {
                    // assuming we are at en.us already
                    if (num - (int)num == 0.0f && !val.Contains(".")) return new JsonData((int)num);

                    num = (float)Math.Round(num, 6);
                    return new JsonData(num);
                }

                var str = val.m_string;
                return new JsonData(str);
                throw new IndexOutOfRangeException();
            }

            if (spec is LSL_String)
                spec = ((LSL_String)spec).m_string;
            else if (spec is LSL_Integer)
                spec = ((LSL_Integer)spec).value;

            if (spec is int ||
                (spec is string && (string)spec == ScriptBaseClass.JSON_APPEND))
            {
                if (spec is int && (int)spec != 0)
                    throw new IndexOutOfRangeException();
                var newdata = new JsonData();
                newdata.SetJsonType(JsonType.Array);
                newdata.Add(JsonBuildRestOfSpec(specifiers, level + 1, val));
                return newdata;
            }

            if (spec is string)
            {
                var newdata = new JsonData();
                newdata.SetJsonType(JsonType.Object);
                IOrderedDictionary no = newdata;
                no.Add((string)spec, JsonBuildRestOfSpec(specifiers, level + 1, val));
                return newdata;
            }

            throw new IndexOutOfRangeException();
        }

        private bool JsonFind(JsonData elem, LSL_List specifiers, int level, out JsonData value)
        {
            value = null;
            if (elem == null)
                return false;

            object spec;
            spec = specifiers.Data[level];

            var haveVal = false;
            JsonData next = null;

            if (elem.GetJsonType() == JsonType.Array)
            {
                if (spec is LSL_Integer)
                {
                    int indx = (LSL_Integer)spec;
                    if (indx >= 0 && indx < elem.Count)
                    {
                        haveVal = true;
                        next = elem[indx];
                    }
                }
            }
            else if (elem.GetJsonType() == JsonType.Object)
            {
                if (spec is LSL_String)
                {
                    IOrderedDictionary e = elem;
                    string key = (LSL_String)spec;
                    if (e.Contains(key))
                    {
                        haveVal = true;
                        next = (JsonData)e[key];
                    }
                }
            }

            if (haveVal)
            {
                if (level == specifiers.Data.Length - 1)
                {
                    value = next;
                    return true;
                }

                level++;
                if (next == null)
                    return false;

                var nextType = next.GetJsonType();
                if (nextType != JsonType.Object && nextType != JsonType.Array)
                    return false;

                return JsonFind(next, specifiers, level, out value);
            }

            return false;
        }

        private LSL_String JsonElementToString(JsonData elem)
        {
            if (elem == null)
                return ScriptBaseClass.JSON_NULL;

            var elemType = elem.GetJsonType();
            switch (elemType)
            {
                case JsonType.Array:
                    return new LSL_String(JsonMapper.ToJson(elem));
                case JsonType.Boolean:
                    return new LSL_String((bool)elem ? ScriptBaseClass.JSON_TRUE : ScriptBaseClass.JSON_FALSE);
                case JsonType.Double:
                    var d = (double)elem;
                    var sd = string.Format(Culture.FormatProvider, "{0:0.0#####}", d);
                    return new LSL_String(sd);
                case JsonType.Int:
                    var i = (int)elem;
                    return new LSL_String(i.ToString());
                case JsonType.Long:
                    var l = (long)elem;
                    return new LSL_String(l.ToString());
                case JsonType.Object:
                    return new LSL_String(JsonMapper.ToJson(elem));
                case JsonType.String:
                    var s = (string)elem;
                    return new LSL_String(s);
                case JsonType.None:
                    return ScriptBaseClass.JSON_NULL;
                default:
                    return ScriptBaseClass.JSON_INVALID;
            }
        }

        private struct Tri
        {
            public Vector3 p1;
            public Vector3 p2;
            public Vector3 p3;
        }

        /// <summary>
        ///     Struct for transmitting parameters required for finding llCastRay ray hits.
        /// </summary>
        public struct RayTrans
        {
            public UUID PartId;
            public UUID GroupId;
            public int Link;
            public Vector3 ScalePart;
            public Vector3 PositionPart;
            public Quaternion RotationPart;
            public bool ShapeNeedsEnds;
            public Vector3 Position1Ray;
            public Vector3 Position1RayProj;
            public Vector3 VectorRayProj;
        }

        /// <summary>
        ///     Struct for llCastRay ray hits.
        /// </summary>
        public struct RayHit
        {
            public UUID PartId;
            public UUID GroupId;
            public int Link;
            public Vector3 Position;
            public Vector3 Normal;
            public float Distance;
        }

        /// <summary>
        ///     Struct for llCastRay throttle data.
        /// </summary>
        public struct CastRayCall
        {
            public UUID RegionId;
            public UUID UserId;
            public int CalledMs;
            public int UsedMs;
        }
    }
}