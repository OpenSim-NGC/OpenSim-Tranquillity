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
// Ubit Umarov 2012
//
// Adapted 2026 by Legion Builds: ubODE's ODESitAvatar (OpenSim.Region.PhysicsModules.ubODE/ODESitAvatar.cs), the sit
// on a part with no sit target, for the Jolt module. The rules and constants are ubODE's, line for line. What changed:
// the part's pose is read from the Jolt part instead of the ODE geometry, its rays go through JoltScene.RaycastActor
// (Jolt's ray cast against that part's body alone), the sphere rule is taken for a part Jolt cooks as a native sphere,
// and the class takes a JoltScene.
using OpenSim.Region.PhysicsModules.SharedBase;
using OpenMetaverse;

namespace OpenSim.Region.PhysicsModules.Jolt;

/// <summary>
/// Where an avatar sits on a part with no sit target: the surface the viewer's click points at, seen from the camera.
/// The answer goes to the callback core passes (ScenePresence.PhysicsSitResponse): status below 0 ignore, 0 no suitable
/// surface (core sends its alert), 1 seated, 3 seated on a wide top with no edge in reach (core plays SIT_GROUND); the
/// offset and orientation are relative to the part.
/// </summary>
internal sealed class JoltSitAvatar
{
    private readonly JoltScene m_scene;

    public JoltSitAvatar(JoltScene pScene)
    {
        m_scene = pScene;
    }

    private const RayFilterFlags RaySitFlags = RayFilterFlags.AllPrims | RayFilterFlags.ClosestHit;

    private static void RotAroundZ(float x, float y, ref Quaternion ori)
    {
        float ang = 0.5f * MathF.Atan2(y, x);
        float s = MathF.Sin(ang);
        float c = MathF.Cos(ang);

        ori.X = 0;
        ori.Y = 0;
        ori.Z = s;
        ori.W = c;
    }

    public void Sit(PhysicsActor actor, Vector3 avPos, Vector3 avCameraPosition, Vector3 offset, Vector3 avOffset, SitAvatarCallback PhysicsSitResponse)
    {
        // ubODE: an actor that is not in the scene, or has no geometry, is ignored. Here: not a prim of this scene, removed,
        // or with no body to test (its own, or its linkset root's).
        if (actor is not JoltPrim prim || prim.IsRemoved || !prim.PartBody.IsValid)
        {
            PhysicsSitResponse(-1, actor?.LocalID ?? 0, offset, Quaternion.Identity);
            return;
        }

        // ubODE reads the geometry's pose; a Jolt part's pose is its own, a welded child's carried by its root's body.
        Vector3 geopos = prim.Position;
        Quaternion geomOri = prim.Orientation;

        Quaternion geomInvOri = Quaternion.Conjugate(in geomOri);
        Quaternion ori = Quaternion.Identity;

        Vector3 rayDir = geopos + offset - avCameraPosition;

        float raylen = rayDir.Length();
        if (raylen < 0.001f)
        {
            PhysicsSitResponse(-1, actor.LocalID, offset, Quaternion.Identity);
            return;
        }

        float t = 1 / raylen;
        rayDir.X *= t;
        rayDir.Y *= t;
        rayDir.Z *= t;

        raylen += 30f; // focal point may be far

        List<ContactResult> rayResults = m_scene.RaycastActor(actor, avCameraPosition, rayDir, raylen, 1, RaySitFlags);
        if (rayResults.Count == 0)
        {
            /* if this fundamental ray failed, then just fail so user can try another spot and not be sitted far on a big prim
            d.AABB aabb;
            d.GeomGetAABB(geom, out aabb);
            offset = new Vector3(avOffset.X, 0, aabb.MaxZ + avOffset.Z - geopos.Z);
            ori = geomInvOri;
            offset *= geomInvOri;
            PhysicsSitResponse(1, actor.LocalID, offset, ori);
            */
            PhysicsSitResponse(0, actor.LocalID, offset, ori);
            return;
        }

        int status = 1;

        offset = rayResults[0].Pos - geopos;

        // ubODE gives a sphere its own rule when the part is an ODE sphere geometry: a sphere prim of equal size on every
        // axis with no cuts. Jolt cooks exactly that part as its native sphere ("sphere" kind), with radius size.X / 2.
        if (prim.ShapeKind == "sphere")
        {
            float r = prim.Size.X * 0.5f;

            offset.Normalize();
            offset *= r;

            RotAroundZ(offset.X, offset.Y, ref ori);

            if (r < 0.4f)
            {
                offset = new Vector3(0, 0, r);
            }
            else
            {
                if (offset.Z < 0.4f)
                {
                    t = offset.Z;
                    float rsq = r * r;

                    t = 1.0f / (rsq - t * t);
                    offset.X *= t;
                    offset.Y *= t;
                    offset.Z = 0.4f;
                    t = rsq - 0.16f;
                    offset.X *= t;
                    offset.Y *= t;
                }
                else if (r > 0.8f && offset.Z > 0.8f * r)
                {
                    status = 3;
                    avOffset.X = -avOffset.X;
                    avOffset.Z *= 1.6f;
                }
            }

            offset += avOffset * ori;

            ori = geomInvOri * ori;
            offset *= geomInvOri;

            PhysicsSitResponse(status, actor.LocalID, offset, ori);
            return;
        }

        Vector3 norm = rayResults[0].Normal;

        if (norm.Z < -0.4f)
        {
            PhysicsSitResponse(0, actor.LocalID, offset, Quaternion.Identity);
            return;
        }


        float SitNormX = -rayDir.X;
        float SitNormY = -rayDir.Y;

        Vector3 pivot = geopos + offset;

        float edgeNormalX = norm.X;
        float edgeNormalY = norm.Y;
        float edgeDirX = -rayDir.X;
        float edgeDirY = -rayDir.Y;
        Vector3 edgePos = rayResults[0].Pos;
        float edgeDist = float.MaxValue;

        bool foundEdge = false;

        if (norm.Z < 0.5f)
        {
            float rayDist = 4.0f;

            for (int i = 0; i < 6; i++)
            {
                pivot.X -= 0.01f * norm.X;
                pivot.Y -= 0.01f * norm.Y;
                pivot.Z -= 0.01f * norm.Z;

                rayDir.X = -norm.X * norm.Z;
                rayDir.Y = -norm.Y * norm.Z;
                rayDir.Z = 1.0f - norm.Z * norm.Z;
                rayDir.Normalize();

                rayResults = m_scene.RaycastActor(actor, pivot, rayDir, rayDist, 1, RayFilterFlags.AllPrims);
                if (rayResults.Count == 0)
                    break;

                if (MathF.Abs(rayResults[0].Normal.Z) < 0.7f)
                {
                    rayDist -= rayResults[0].Depth;
                    if (rayDist < 0f)
                        break;

                    pivot = rayResults[0].Pos;
                    norm = rayResults[0].Normal;
                    edgeNormalX = norm.X;
                    edgeNormalY = norm.Y;
                    edgeDirX = -rayDir.X;
                    edgeDirY = -rayDir.Y;
                }
                else
                {
                    foundEdge = true;
                    edgePos = rayResults[0].Pos;
                    break;
                }
            }

            if (!foundEdge)
            {
                PhysicsSitResponse(0, actor.LocalID, offset, ori);
                return;
            }
            avOffset.X *= 0.5f;
        }

        else if (norm.Z > 0.866f)
        {
            float toCamBaseX = avCameraPosition.X - pivot.X;
            float toCamBaseY = avCameraPosition.Y - pivot.Y;
            float toCamX = toCamBaseX;
            float toCamY = toCamBaseY;

            for (int j = 0; j < 4; j++)
            {
                float rayDist = 1.0f;
                float curEdgeDist = 0.0f;

                for (int i = 0; i < 3; i++)
                {
                    pivot.Z -= 0.01f;
                    rayDir.X = toCamX;
                    rayDir.Y = toCamY;
                    rayDir.Z = (-toCamX * norm.X - toCamY * norm.Y) / norm.Z;
                    rayDir.Normalize();

                    rayResults = m_scene.RaycastActor(actor, pivot, rayDir, rayDist, 1, RayFilterFlags.AllPrims);
                    if (rayResults.Count == 0)
                        break;

                    curEdgeDist += rayResults[0].Depth;

                    if (rayResults[0].Normal.Z > 0.5f)
                    {
                        rayDist -= rayResults[0].Depth;
                        if (rayDist < 0f)
                            break;

                        pivot = rayResults[0].Pos;
                        norm = rayResults[0].Normal;
                    }
                    else
                    {
                        foundEdge = true;
                        if (curEdgeDist < edgeDist)
                        {
                            edgeDist = curEdgeDist;
                            edgeNormalX = rayResults[0].Normal.X;
                            edgeNormalY = rayResults[0].Normal.Y;
                            edgeDirX = rayDir.X;
                            edgeDirY = rayDir.Y;
                            edgePos = rayResults[0].Pos;
                        }
                        break;
                    }
                }
                if (foundEdge && edgeDist < 0.2f)
                    break;

                pivot = geopos + offset;

                switch (j)
                {
                    case 0:
                        toCamX = -toCamBaseY;
                        toCamY = toCamBaseX;
                        break;
                    case 1:
                        toCamX = toCamBaseY;
                        toCamY = -toCamBaseX;
                        break;
                    case 2:
                        toCamX = -toCamBaseX;
                        toCamY = -toCamBaseY;
                        break;
                    default:
                        break;
                }
            }

            if (!foundEdge)
            {
                avOffset.X = -avOffset.X;
                avOffset.Z *= 1.6f;

                RotAroundZ(SitNormX, SitNormY, ref ori);

                offset += avOffset * ori;

                ori = geomInvOri * ori;
                offset *= geomInvOri;

                PhysicsSitResponse(3, actor.LocalID, offset, ori);
                return;
            }
            avOffset.X *= 0.5f;
        }

        SitNormX = edgeNormalX;
        SitNormY = edgeNormalY;
        if (edgeDirX * SitNormX + edgeDirY * SitNormY < 0)
        {
            SitNormX = -SitNormX;
            SitNormY = -SitNormY;
        }

        RotAroundZ(SitNormX, SitNormY, ref ori);

        offset = edgePos + avOffset * ori;
        offset -= geopos;

        ori = geomInvOri * ori;
        offset *= geomInvOri;

        PhysicsSitResponse(1, actor.LocalID, offset, ori);
        return;
    }
}
