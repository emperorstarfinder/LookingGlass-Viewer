﻿/* Copyright (c) 2008 Robert Adams
 * Redistribution and use in source and binary forms, with or without
 * modification, are permitted provided that the following conditions are met:
 *     * Redistributions of source code must retain the above copyright
 *       notice, this list of conditions and the following disclaimer.
 *     * Redistributions in binary form must reproduce the above copyright
 *       notice, this list of conditions and the following disclaimer in the
 *       documentation and/or other materials provided with the distribution.
 *     * The name of the copyright holder may not be used to endorse or promote products
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
using System.Collections.Generic;
using System.Drawing;
using System.Text;
using LookingGlass;
using LookingGlass.Framework.Logging;
using LookingGlass.Renderer;
using LookingGlass.World;
using LookingGlass.World.LL;
using OMV = OpenMetaverse;
using OMVR = OpenMetaverse.Rendering;

namespace LookingGlass.Renderer.Ogr {

public class RendererOgreLL : IWorldRenderConv {
    private ILog m_log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType.Name);

    private float m_sceneMagnification = 1.0f;
    bool m_buildMaterialsAtMeshCreationTime = false;
    bool m_buildMaterialsAtRenderInfoTime = true;

    // we've added things to the interface for scuplties. Need to push back into OMV someday.
    // private OMVR.IRendering m_meshMaker = null;
    private Mesher.MeshmerizerR m_meshMaker = null;
    // set to true if the generation of the mesh includes the scale factors
    private bool m_useRendererMeshScaling;
    private bool m_useRendererTextureScaling;

    static RendererOgreLL m_instance = null;
    public static RendererOgreLL Instance {
        get {
            if (m_instance == null) {
                m_instance = new RendererOgreLL();
            }
            return m_instance;
        }
    }

    public RendererOgreLL() {
        if (m_meshMaker == null) {
            Renderer.Mesher.MeshmerizerR amesher = new Renderer.Mesher.MeshmerizerR();
            // There is two ways to do scaling: in the mesh or in Ogre. We choose the latter here
            // so we can create shared vertices for the standard shapes (the cubes  that are everywhere)
            // this causes the mesherizer to not scale the node coordinates by the prim scaling factor
            // Update: scaling with Ogre has proved problematic: the scaling effects the mesh and
            // the position coordinates around the object. This is a problem for child nodes.
            // It also effects the texture mapping so texture scaling factors would have to be
            // scaled by the scale of teh face that they appear on. Ugh.
            // For the moment, turned off while I figure that stuff out.
            // m_useRendererMeshScaling = true; // use Ogre to scale the mesh
            m_useRendererMeshScaling = false; // scale the mesh in the meshmerizer
            amesher.ShouldScaleMesh = !m_useRendererMeshScaling;
            m_useRendererTextureScaling = false; // use software texture face scaling
            // m_useRendererTextureScaling = true; // use Ogre texture scaling rather than computing it
            m_meshMaker = amesher;
        }

        // magnification of passed World coordinates into Ogre coordinates
        m_sceneMagnification = float.Parse(LookingGlassBase.Instance.AppParams.ParamString("Renderer.Ogre.LL.SceneMagnification"));
        // true if to creat materials while we are creating the mesh
        m_buildMaterialsAtMeshCreationTime = LookingGlassBase.Instance.AppParams.ParamBool("Renderer.Ogre.LL.EarlyMaterialCreate");
        m_buildMaterialsAtRenderInfoTime = LookingGlassBase.Instance.AppParams.ParamBool("Renderer.Ogre.LL.RenderInfoMaterialCreate");
    }

    /// <summary>
    /// Collect rendering info. The information collected for rendering has a pre
    /// phase (this call), a doit phase and then a post phase (usually on demand
    /// requests).
    /// If we can't collect all the information return null. For LLLP, the one thing
    /// we might not have is the parent entity since child prims are rendered relative
    /// to the parent.
    /// This will be called multiple times trying to get the information for the 
    /// renderable. The callCount is the number of times we have asked. The caller
    /// can pass zero and know nothing will happen. Values more than zero can cause
    /// this routine to try and do some implementation specific thing to fix the
    /// problem. For LLLP, this is usually asking for the parent to be loaded.
    /// </summary>
    /// <param name="sceneMgr"></param>
    /// <param name="ent"></param>
    /// <param name="callCount">zero if do nothing, otherwise the number of times that
    /// this RenderingInfo has been asked for</param>
    /// <returns>rendering info or null if we cannot collect all data</returns>
    public RenderableInfo RenderingInfo(int priority, Object sceneMgr, IEntity ent, int callCount) {
        LLEntityBase llent;
        LLRegionContext rcontext;
        OMV.Primitive prim;
        string newMeshName = EntityNameOgre.ConvertToOgreNameX(ent.Name, ".mesh");
        // true if we should do the scaling with the rendering parameters
        bool shouldHaveRendererScale = m_useRendererMeshScaling;

        try {
            llent = (LLEntityBase)ent;
            rcontext = (LLRegionContext)llent.RegionContext;
            prim = llent.Prim;
            if (prim == null) throw new LookingGlassException("ASSERT: RenderOgreLL: prim is null");
        }
        catch (Exception e) {
            m_log.Log(LogLevel.DRENDERDETAIL, "RenderingInfoLL: conversion of pointers failed: " + e.ToString());
            throw e;
        }

        RenderableInfo ri = new RenderableInfo();
        ri.basicObject = newMeshName;   // pass the name of the mesh that should be created
        
        // if a standard type (done by Ogre), let the rendering system do the scaling
        int meshType = 0;
        int meshFaces = 0;
        if (CheckStandardMeshType(prim, out meshType, out meshFaces)) {
            // if a standard mesh type, use Ogre scaling so we can reuse base shapes
            shouldHaveRendererScale = true;
        }

        // if the prim has a parent, we must hang this scene node off the parent's scene node
        if (prim.ParentID != 0) {
            if (!rcontext.TryGetEntityLocalID(prim.ParentID, out ri.parentEntity)) {
                // we can't find the parent. Can't build render info.
                // if we've been waiting for that parent, ask for same
                if ((callCount != 0) && ((callCount % 3) == 0)) {
                    rcontext.RequestLocalID(prim.ParentID);
                }
                return null;
            }
        }
        
        ri.rotation = prim.Rotation;
        ri.position = prim.Position;

        // If the mesh was scaled just pass the renderer a scale of one
        // otherwise, if the mesh was not scaled, have the renderer do the scaling
        // This specifies what we want the renderer to do
        if (shouldHaveRendererScale) {
            ri.scale = prim.Scale * m_sceneMagnification;
        }
        else {
            ri.scale = new OMV.Vector3(m_sceneMagnification, m_sceneMagnification, m_sceneMagnification);
        }

        // while we're in the neighborhood, we can create the materials
        if (m_buildMaterialsAtRenderInfoTime) {
            CreateMaterialResource6(priority, ent, prim);
        }

        return ri;
    }

    /// <summary>
    /// Create a mesh in the renderer.
    /// </summary>
    /// <param name="sMgr">the scene manager receiving  the mesh</param>
    /// <param name="ent">The entity the mesh is coming from</param>
    /// <param name="meshName">The name the mesh should take</param>
    public bool CreateMeshResource(int priority, IEntity ent, string meshName) {
        LLEntityPhysical llent;
        OMV.Primitive prim;

        try {
            llent = (LLEntityPhysical)ent;
            prim = llent.Prim;
            if (prim == null) throw new LookingGlassException("ASSERT: RenderOgreLL: prim is null");
        }
        catch (Exception e) {
            m_log.Log(LogLevel.DRENDERDETAIL, "CreateMeshResource: conversion of pointers failed: " + e.ToString());
            throw e;
        }

        int meshType = 0;
        int meshFaces = 0;
        // TODO: This is the start of come code to specialize the creation of standard
        // meshes. Since a large number of prims are cubes, they can share the face vertices
        // and thus reduce the total number of Ogre's vertices stored.
        // At the moment, CheckStandardMeshType returns false so we don't do anything special yet
        if (CheckStandardMeshType(prim, out meshType, out meshFaces)) {
            m_log.Log(LogLevel.DBADERROR, "CreateMeshResource: not implemented Standard Type");
            /*
            // while we're in the neighborhood, we can create the materials
            if (m_buildMaterialsAtMeshCreationTime) {
                for (int j = 0; j < meshFaces; j++) {
                    CreateMaterialResource2(ent, prim, EntityNameOgre.ConvertToOgreMaterialNameX(ent.Name, j), j);
                }
            }

            Ogr.CreateStandardMeshResource(meshName, meshType);
             */
        }
        else {
            OMVR.FacetedMesh mesh;
            try {

                if (prim.Sculpt != null) {
                    // looks like it's a sculpty. Do it that way
                    EntityNameLL textureEnt = EntityNameLL.ConvertTextureWorldIDToEntityName(ent.AssetContext, prim.Sculpt.SculptTexture);
                    System.Drawing.Bitmap textureBitmap = ent.AssetContext.GetTexture(textureEnt);
                    if (textureBitmap == null) {
                        m_log.Log(LogLevel.DRENDERDETAIL, "CreateMeshResource: waiting for texture for sculpty {0}", ent.Name.Name);
                        // Don't have the texture now so ask for the texture to be loaded.
                        // Note that we ignore the callback and let the work queue requeing get us back here
                        ent.AssetContext.DoTextureLoad(textureEnt, AssetContextBase.AssetType.SculptieTexture, 
                            delegate(string name, bool trans) { return; });
                        // This will cause the work queue to requeue the mesh creation and call us
                        //   back later to retry creating the mesh
                        return false;
                    }
                    m_log.Log(LogLevel.DRENDERDETAIL, "CreateMeshResource: mesherizing scuplty {0}", ent.Name.Name);
                    // mesh = m_meshMaker.GenerateSculptMesh(textureBitmap, prim, OMVR.DetailLevel.Highest);
                    mesh = m_meshMaker.GenerateSculptMesh(textureBitmap, prim, OMVR.DetailLevel.Medium);
                    if (mesh.Faces.Count > 10) {
                        m_log.Log(LogLevel.DBADERROR, "CreateMeshResource: mesh has {0} faces!!!!", mesh.Faces.Count);
                    }
                    textureBitmap.Dispose();
                }
                else {
                    // we really should use Low for boxes, med for most things and high for megaprim curves
                    // OMVR.DetailLevel meshDetail = OMVR.DetailLevel.High;
                    OMVR.DetailLevel meshDetail = OMVR.DetailLevel.Medium;
                    if (prim.Type == OMV.PrimType.Box) {
                        meshDetail = OMVR.DetailLevel.Low;
                        // m_log.Log(LogLevel.DRENDERDETAIL, "CreateMeshResource: Low detail for {0}", ent.Name.Name);
                    }
                    mesh = m_meshMaker.GenerateFacetedMesh(prim, meshDetail);
                    if (mesh.Faces.Count > 10) {
                        m_log.Log(LogLevel.DBADERROR, "CreateMeshResource: mesh has {0} faces!!!!", mesh.Faces.Count);
                    }
                }
            }
            catch (Exception e) {
                m_log.Log(LogLevel.DRENDERDETAIL, "CreateMeshResource: failed mesh generate for {0}: {1}", 
                    ent.Name.Name, e.ToString());
                throw e;
            }

            // we have the face data. We package this up into a few big arrays to pass them
            //   to the real renderer.

            // we pass two one-dimensional arrays of floating point numbers over to the
            // unmanaged code. The first array contains:
            //   faceCounts[0] = total number of int's in this array (for alloc and freeing in Ogre)
            //   faceCounts[1] = number of faces
            //   faceCounts[2] = offset in second array for beginning of vertex info for face 1
            //   faceCounts[3] = number of vertices for face 1
            //   faceCounts[4] = stride for vertex info for face 1 (= 8)
            //   faceCounts[5] = offset in second array for beginning of indices info for face 1
            //   faceCounts[6] = number of indices for face 1
            //   faceCounts[7] = stride for indices (= 3)
            //   faceCounts[8] = offset in second array for beginning of vertex info for face 2
            //   faceCounts[9] = number of vertices for face 2
            //   faceCounts[10] = stride for vertex info for face 2 (= 8)
            //   etc
            // The second array contains the vertex info in the order:
            //   v.X, v.Y, v.Z, t.X, t.Y, n.X, n.Y, n.Z
            // this is repeated for each vertex
            // This is followed by the list of indices listed as i.X, i.Y, i.Z

            const int faceCountsStride = 6;
            const int verticesStride = 8;
            const int indicesStride = 3;
            // calculate how many floating point numbers we're pushing over
            int[] faceCounts = new int[mesh.Faces.Count * faceCountsStride + 2];
            faceCounts[0] = faceCounts.Length;
            faceCounts[1] = mesh.Faces.Count;
            int totalVertices = 0;
            for (int j = 0; j < mesh.Faces.Count; j++) {
                OMVR.Face face = mesh.Faces[j];
                int faceBase = j * faceCountsStride + 2;
                // m_log.Log(LogLevel.DRENDERDETAIL, "Mesh F" + j.ToString() + ":"
                //     + " vcnt=" + face.Vertices.Count.ToString()
                //     + " icnt=" + face.Indices.Count.ToString());
                faceCounts[faceBase + 0] = totalVertices;
                faceCounts[faceBase + 1] = face.Vertices.Count;
                faceCounts[faceBase + 2] = verticesStride;
                totalVertices += face.Vertices.Count * verticesStride;
                faceCounts[faceBase + 3] = totalVertices;
                faceCounts[faceBase + 4] = face.Indices.Count;
                faceCounts[faceBase + 5] = indicesStride;
                totalVertices += face.Indices.Count;
            }

            float[] faceVertices = new float[totalVertices+2];
            faceVertices[0] = faceVertices.Length;
            int vertI = 1;
            for (int j = 0; j < mesh.Faces.Count; j++) {
                OMVR.Face face = mesh.Faces[j];

                // Texture transform for this face
                OMV.Primitive.TextureEntryFace teFace = face.TextureFace;
                try {
                    if ((teFace != null) && !m_useRendererTextureScaling) {
                        m_meshMaker.TransformTexCoords(face.Vertices, face.Center, teFace);
                    }
                }
                catch {
                    m_log.Log(LogLevel.DBADERROR, "RenderOgreLL.CreateMeshResource:"
                        + " more faces in mesh than in prim:"
                        + " ent=" + ent.Name
                        + ", face=" + j.ToString()
                    );
                }

                // Vertices for this face
                for (int k = 0; k < face.Vertices.Count; k++) {
                    OMVR.Vertex thisVert = face.Vertices[k];
                    // m_log.Log(LogLevel.DRENDERDETAIL, "CreateMesh: vertices: p={0}, t={1}, n={2}",
                    //     thisVert.Position.ToString(), thisVert.TexCoord.ToString(), thisVert.Normal.ToString());
                    faceVertices[vertI + 0] = thisVert.Position.X;
                    faceVertices[vertI + 1] = thisVert.Position.Y;
                    faceVertices[vertI + 2] = thisVert.Position.Z;
                    faceVertices[vertI + 3] = thisVert.TexCoord.X;
                    faceVertices[vertI + 4] = thisVert.TexCoord.Y;
                    faceVertices[vertI + 5] = thisVert.Normal.X;
                    faceVertices[vertI + 6] = thisVert.Normal.Y;
                    faceVertices[vertI + 7] = thisVert.Normal.Z;
                    vertI += verticesStride;
                }
                for (int k = 0; k < face.Indices.Count; k += 3) {
                    faceVertices[vertI + 0] = face.Indices[k + 0];
                    faceVertices[vertI + 1] = face.Indices[k + 1];
                    faceVertices[vertI + 2] = face.Indices[k + 2];
                    vertI += indicesStride;
                }
            }

            // while we're in the neighborhood, we can create the materials
            if (m_buildMaterialsAtMeshCreationTime) {
                for (int j = 0; j < mesh.Faces.Count; j++) {
                    CreateMaterialResource2(priority, ent, prim, EntityNameOgre.ConvertToOgreMaterialNameX(ent.Name, j), j);
                }
            }

            m_log.Log(LogLevel.DRENDERDETAIL, "RenderOgreLL: "
                + ent.Name
                + " f=" + mesh.Faces.Count.ToString()
                + " fcs=" + faceCounts.Length
                + " fs=" + faceVertices.Length
                + " vi=" + vertI
                );
            // Now create the mesh
            Ogr.CreateMeshResourceBF(priority, meshName, faceCounts, faceVertices);
        }
        return true;
    }

    // Examine the prim and see if it's a standard shape that we can pass to Ogre to implement
    // in a standard way. This is most useful for cubes which don't change and are just 
    // scaled along their dimensions.
    private bool CheckStandardMeshType(OMV.Primitive prim, out int meshType, out int meshFaces) {
        meshType = 0;
        meshFaces = 0;
        return false;
    }

    public void CreateMaterialResource(int priority, IEntity ent, string materialName) {
        LLEntityPhysical llent;
        OMV.Primitive prim;

        try {
            llent = (LLEntityPhysical)ent;
            prim = llent.Prim;
            if (prim == null) throw new LookingGlassException("ASSERT: RenderOgreLL: prim is null");
        }
        catch (Exception e) {
            m_log.Log(LogLevel.DRENDERDETAIL, "CreateMaterialResource: conversion of pointers failed: " + e.ToString());
            throw e;
        }
        int faceNum = EntityNameOgre.GetFaceFromOgreMaterialNameX(materialName);
        if (faceNum < 0) {
            // no face was found in the material name
            m_log.Log(LogLevel.DRENDERDETAIL, "CreateMaterialResource: no face number for " + materialName);
            return;
        }
        CreateMaterialResource2(priority, ent, prim, materialName, faceNum);
    }

    /// <summary>
    /// Create a material resource in Ogre. This is the new way done by passing an
    /// array of parameters. Bool values are 0f for false and true otherwise.
    /// The offsets in the passed parameter array is defined with the interface in
    /// LookingGlass.Renderer.Ogre.Ogr.
    /// </summary>
    /// <param name="ent">the entity of the underlying prim</param>
    /// <param name="prim">the OMV.Primitive that is getting the material</param>
    /// <param name="materialName">the name to give the new material</param>
    /// <param name="faceNum">the index of the primitive face getting the material</param>
    private void CreateMaterialResource2(int priority, IEntity ent, OMV.Primitive prim, 
                            string materialName, int faceNum) {
        float[] textureParams = new float[(int)Ogr.CreateMaterialParam.maxParam];
        string textureOgreResourceName = "";
        CreateMaterialParameters(ent, prim, 0, ref textureParams, faceNum, out textureOgreResourceName);
        m_log.Log(LogLevel.DRENDERDETAIL, "CreateMaterialResource2: m=" + materialName + ",o=" + textureOgreResourceName);
        Ogr.CreateMaterialResource2BF(priority, materialName, textureOgreResourceName, textureParams);
    }

    private void CreateMaterialParameters(IEntity ent, OMV.Primitive prim, int pBase, ref float[] textureParams, 
                    int faceNum, out String texName) {
        OMV.Primitive.TextureEntryFace textureFace = prim.Textures.GetFace((uint)faceNum);
        OMV.UUID textureID = OMV.Primitive.TextureEntry.WHITE_TEXTURE;
        if (textureFace != null) {
            textureParams[pBase + (int)Ogr.CreateMaterialParam.colorR] = textureFace.RGBA.R;
            textureParams[pBase + (int)Ogr.CreateMaterialParam.colorG] = textureFace.RGBA.G;
            textureParams[pBase + (int)Ogr.CreateMaterialParam.colorB] = textureFace.RGBA.B;
            textureParams[pBase + (int)Ogr.CreateMaterialParam.colorA] = textureFace.RGBA.A;
            if (m_useRendererTextureScaling) {
                textureParams[pBase + (int)Ogr.CreateMaterialParam.scaleU] = 1f / textureFace.RepeatU;
                textureParams[pBase + (int)Ogr.CreateMaterialParam.scaleV] = 1f / textureFace.RepeatV;
                textureParams[pBase + (int)Ogr.CreateMaterialParam.scrollU] = textureFace.OffsetU;
                textureParams[pBase + (int)Ogr.CreateMaterialParam.scrollV] = -textureFace.OffsetV;
                textureParams[pBase + (int)Ogr.CreateMaterialParam.rotate] = textureFace.Rotation;
            }
            else {
                textureParams[pBase + (int)Ogr.CreateMaterialParam.scaleU] = 1.0f;
                textureParams[pBase + (int)Ogr.CreateMaterialParam.scaleV] = 1.0f;
                textureParams[pBase + (int)Ogr.CreateMaterialParam.scrollU] = 1.0f;
                textureParams[pBase + (int)Ogr.CreateMaterialParam.scrollV] = 1.0f;
                textureParams[pBase + (int)Ogr.CreateMaterialParam.rotate] = 0.0f;
            }
            textureParams[pBase + (int)Ogr.CreateMaterialParam.glow] = textureFace.Glow;
            textureParams[pBase + (int)Ogr.CreateMaterialParam.bump] = (float)textureFace.Bump;
            textureParams[pBase + (int)Ogr.CreateMaterialParam.shiny] = (float)textureFace.Shiny;
            textureParams[pBase + (int)Ogr.CreateMaterialParam.fullBright] = textureFace.Fullbright ? 1f : 0f;
            textureParams[pBase + (int)Ogr.CreateMaterialParam.mappingType] = (float)textureFace.TexMapType;
            textureParams[pBase + (int)Ogr.CreateMaterialParam.mediaFlags] = textureFace.MediaFlags ? 1f : 0f;
            // since we can't calculate whether material is transparent or not (actually
            //   we don't have that information at this instant), assume transparent
            textureParams[pBase + (int)Ogr.CreateMaterialParam.textureHasTransparent] = 0f;
            textureID = textureFace.TextureID;
            // wish I could pass the texture animation information here but that's
            //    in the texture entry and not in the face description
        }
        else {
            textureParams[pBase + (int)Ogr.CreateMaterialParam.colorR] = 0.4f;
            textureParams[pBase + (int)Ogr.CreateMaterialParam.colorG] = 0.4f;
            textureParams[pBase + (int)Ogr.CreateMaterialParam.colorB] = 0.4f;
            textureParams[pBase + (int)Ogr.CreateMaterialParam.colorA] = 0.0f;
            textureParams[pBase + (int)Ogr.CreateMaterialParam.scaleU] = 1.0f;
            textureParams[pBase + (int)Ogr.CreateMaterialParam.scaleV] = 1.0f;
            textureParams[pBase + (int)Ogr.CreateMaterialParam.scrollU] = 1.0f;
            textureParams[pBase + (int)Ogr.CreateMaterialParam.scrollV] = 1.0f;
            textureParams[pBase + (int)Ogr.CreateMaterialParam.rotate] = 0.0f;
            textureParams[pBase + (int)Ogr.CreateMaterialParam.glow] = 0.0f;
            textureParams[pBase + (int)Ogr.CreateMaterialParam.bump] = (float)OMV.Bumpiness.None;
            textureParams[pBase + (int)Ogr.CreateMaterialParam.shiny] = (float)OMV.Shininess.None;
            textureParams[pBase + (int)Ogr.CreateMaterialParam.fullBright] = 0f;
            textureParams[pBase + (int)Ogr.CreateMaterialParam.mappingType] = 0f;
            textureParams[pBase + (int)Ogr.CreateMaterialParam.mediaFlags] = 0f;
            textureParams[pBase + (int)Ogr.CreateMaterialParam.textureHasTransparent] = 0f;
        }
        EntityName textureEntityName = new EntityName(ent, textureID.ToString());
        string textureOgreResourceName = "";
        if (textureID != OMV.Primitive.TextureEntry.WHITE_TEXTURE) {
            textureOgreResourceName = EntityNameOgre.ConvertToOgreNameX(textureEntityName, null);
        }
        texName = textureOgreResourceName;
    }

    public void RebuildEntityMaterials(int priority, IEntity ent) {
        LLEntityBase llent;
        LLRegionContext rcontext;
        OMV.Primitive prim;

        try {
            llent = (LLEntityBase)ent;
            rcontext = (LLRegionContext)llent.RegionContext;
            prim = llent.Prim;
            if (prim == null) throw new LookingGlassException("ASSERT: RenderOgreLL: prim is null");
        }
        catch (Exception e) {
            m_log.Log(LogLevel.DRENDERDETAIL, "RenderingInfoLL: conversion of pointers failed: " + e.ToString());
            throw e;
        }
        CreateMaterialResource6(priority, llent, prim);
    }

    /// <summary>
    /// Create the primary six materials for the prim
    /// </summary>
    /// <param name="ent"></param>
    /// <param name="prim"></param>
    private void CreateMaterialResource6(int priority, IEntity ent, OMV.Primitive prim) {
        // we create the usual ones. extra faces will be asked for on demand
        for (int j = 0; j <= 6; j++) {
            CreateMaterialResource2(priority, ent, prim, EntityNameOgre.ConvertToOgreMaterialNameX(ent.Name, j), j);
        }
    }

    /* Temp not use to see if between frame change is good enough. Don't have two optimizations.
    /// <summary>
    /// Create six of the basic materials for this prim. This is passed to Ogre in one big lump
    /// to make things go a lot quicker.
    /// </summary>
    /// <param name="ent"></param>
    /// <param name="prim"></param>
    private void CreateMaterialResource6X(IEntity ent, OMV.Primitive prim) {
        // we create the usual ones. extra faces will be asked for on demand
        const int genCount = 6;
        float[] textureParams = new float[1 + ((int)Ogr.CreateMaterialParam.maxParam) * genCount];
        string[] materialNames = new string[genCount];
        string[] textureOgreNames = new string[genCount];

        textureParams[0] = (float)Ogr.CreateMaterialParam.maxParam;
        int pBase = 1;
        string textureOgreName;
        for (int j = 0; j < genCount; j++) {
            CreateMaterialParameters(ent, prim, pBase, ref textureParams, j, out textureOgreName);
            materialNames[j] = EntityNameOgre.ConvertToOgreMaterialNameX(ent.Name, j);
            textureOgreNames[j] = textureOgreName;
            pBase += (int)textureParams[0];
        }
        Ogr.CreateMaterialResource6(
            materialNames[0], materialNames[1], materialNames[2], materialNames[3], materialNames[4], materialNames[5],
            textureOgreNames[0], textureOgreNames[1], textureOgreNames[2], textureOgreNames[3], textureOgreNames[4], textureOgreNames[5],
            textureParams
        );
    }
     */

    /// <summary>
    /// We have a new region to place in the view. Create the scene node for the 
    /// whole region.
    /// </summary>
    /// <param name="sMgr"></param>
    /// <param name="rcontext"></param>
    public void MapRegionIntoView(int priority, Object sMgr, IRegionContext rcontext) {
        OgreSceneMgr m_sceneMgr = (OgreSceneMgr)sMgr;
        if (rcontext is LLRegionContext) {
            // a SL compatible region
            LLRegionContext llrcontext = (LLRegionContext)rcontext;
            // if we don't have a region scene node create one
            if (RendererOgre.GetRegionSceneNode(llrcontext) == null) {
                // this funny rotation of the region's scenenode causes the region
                // to be twisted from LL coordinates (Z up) to Ogre coords (Y up)
                // Anything added under this node will not need to be converted.
                OMV.Quaternion orient = OMV.Quaternion.CreateFromAxisAngle(OMV.Vector3.UnitX, -Constants.PI / 2);

                m_log.Log(LogLevel.DRENDERDETAIL, "MapRegionIntoView: Region at {0}, {1}, {2}",
                        (float)rcontext.WorldBase.X * m_sceneMagnification,
                        (float)rcontext.WorldBase.Z * m_sceneMagnification,
                        -(float)rcontext.WorldBase.Y * m_sceneMagnification
                        );

                OgreSceneNode node = m_sceneMgr.CreateSceneNode(EntityNameOgre.ConvertToOgreSceneNodeName(rcontext.Name),
                        null,        // because NULL, will add to root
                        false, true,
                        (float)rcontext.WorldBase.X * m_sceneMagnification,
                        (float)rcontext.WorldBase.Z * m_sceneMagnification,
                        -(float)rcontext.WorldBase.Y * m_sceneMagnification,
                        m_sceneMagnification, m_sceneMagnification, m_sceneMagnification,
                        // 1f, 1f, 1f,
                        orient.W, orient.X, orient.Y, orient.Z);

                // the region scene node is saved in the region context additions
                llrcontext.SetAddition(RendererOgre.AddRegionSceneNode, node);

                // Terrain will be added as we get the messages describing same

                // if the region has water, add that
                if (rcontext.TerrainInfo.WaterHeight != TerrainInfoBase.NOWATER) {
                    Ogr.AddOceanToRegion(m_sceneMgr.BasePtr, node.BasePtr,
                                rcontext.Size.X * m_sceneMagnification, 
                                rcontext.Size.Y * m_sceneMagnification,
                                rcontext.TerrainInfo.WaterHeight, 
                                "Water/" + rcontext.Name);
                }
            }
        }
        return;
    }
}
}
