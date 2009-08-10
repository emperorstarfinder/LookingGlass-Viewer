﻿/* Copyright (c) Robert Adams
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
// using System.Drawing;
using System.Text;
using LookingGlass.World;
using OMV = OpenMetaverse;

namespace LookingGlass.Renderer {
    /// <summary>
    /// The origional Mogre renderer design did the mesh generation before creating
    /// the scene node. Scene node creation has to happen between frames because
    /// you get crashes if you didn't with the scene graph while it's being rendered.
    /// So, a call is made to generate all the things needed for the scene node
    /// and these are returned in a RenderableInfo. Between frames, the scene node
    /// is created and it is decorated with all the information from RenderableInfo.
    /// For Mogre, 'basicObject' was usually a Mogre::MovableObject.
    /// 
    /// The Ogre renderer design has the mesh generated by demand after the
    /// scene node is created. In this post generation model, RenderableInfo
    /// is not used or is just passed for compatability.
    /// </summary>
    public class RenderableInfo {
        public Object basicObject;
        public uint parentID;
        public OMV.Vector3 position;
        public OMV.Quaternion rotation;
        public OMV.Vector3 scale;
        public Object RegionRoot;
        public RenderableInfo() {
            basicObject = null; RegionRoot = null;
            parentID = 0;
            position = OMV.Vector3.Zero;
            rotation = new OMV.Quaternion(0f, 0f, 0f, 0f);
            scale = OMV.Vector3.Zero;
        }
    }

public struct FaceData {
        public float[] Vertices;
        public ushort[] Indices;
        public float[] TexCoords;
        // public System.Drawing.Image Texture;
        // TODO: Normals / binormals?
    }

    public enum InputModeCode {
        ModeMainKeys,   // focus on main display, input is keystrokes
        MainKeysMouse,  // focus on main display, input is keystrokes and mouse
        MainSelect,     // focus on main display, select objects
        OverlayNext,    // focus on next overlay
        OverlayLast,    // focus on next overlay
    };


    public delegate void RendererBeforeFrameCallback();

    public interface IRenderProvider {
        event RendererBeforeFrameCallback OnRendererBeforeFrame;

        IUserInterfaceProvider UserInterface { get; }

        // entry for main thread for rendering. Return false if you don't need it.
        bool RendererThread();

        // Set the entity to be rendered
        void Render(IEntity ent);
        void UnRender(IEntity ent);

        // tell the renderer about the camera position
        void UpdateCamera(CameraControl cam);
        void UpdateEnvironmentalLights(EntityLight sun, EntityLight moon);
        // TODO: ambient setting

        // Given the current mouse position, return a point in the world
        OMV.Vector3d SelectPoint();

        // called when a new region is found, decorates the region context with
        // rendering specific information for placing in  the view
        void MapRegionIntoView(RegionContextBase rcontext);

        // something about the terrain has changed, do some updating
        void UpdateTerrain(RegionContextBase wcontext);
    }
}
