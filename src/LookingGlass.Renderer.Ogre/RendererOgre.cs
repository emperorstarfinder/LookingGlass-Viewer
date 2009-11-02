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
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Text.RegularExpressions;
using System.Windows.Forms;     // used for the Keys class
using LookingGlass.Framework;
using LookingGlass.Framework.Logging;
using LookingGlass.Framework.Parameters;
using LookingGlass.Framework.Modules;
using LookingGlass.Framework.Statistics;
using LookingGlass.Framework.WorkQueue;
using LookingGlass.Renderer;
using LookingGlass.Rest;
using LookingGlass.World;
using OMV = OpenMetaverse;
using OMVSD = OpenMetaverse.StructuredData;

namespace LookingGlass.Renderer.Ogr {

public class RendererOgre : ModuleBase, IRenderProvider {
    private ILog m_log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType.Name);
    private ILog m_logOgre = LogManager.GetLogger("RendererCpp");

# pragma warning disable 0067   // disable unused event warning
    public event RendererBeforeFrameCallback OnRendererBeforeFrame;
# pragma warning restore 0067

    // we decorate IEntities with SceneNodes. This is our slot in the IEntity addition table
    // SceneNode of the entity itself
    public static int AddSceneNodeName;
    public static string AddSceneNodeNameName = "OgreSceneNodeName";
    public static string GetSceneNodeName(IEntity ent) {
        return (string)ent.Addition(RendererOgre.AddSceneNodeName);
    }
    // SceneNode of the region if this is a IRegionContext
    public static int AddRegionSceneNode;
    public static string AddRegionSceneNodeName = "OgreRegionSceneNode";
    public static OgreSceneNode GetRegionSceneNode(IEntity ent) {
        return (OgreSceneNode)ent.Addition(RendererOgre.AddRegionSceneNode);
    }
    // SceneNode of  the  terrain if this is an ITerrainInfo
    public static int AddTerrainSceneNode;
    public static string AddTerrainSceneNodeName = "OgreTerrainSceneNode";
    public static OgreSceneNode GetTerrainSceneNode(IEntity ent) {
        return (OgreSceneNode)ent.Addition(RendererOgre.AddTerrainSceneNode);
    }
    // Instance if IWorldRenderConv for converting this entity to my renderer
    public static int AddWorldRenderConv;
    public static string AddWorldRenderConvName = "OgreWorldRenderConvName";
    public static IWorldRenderConv GetWorldRenderConv(IEntity ent) {
        return (IWorldRenderConv)ent.Addition(RendererOgre.AddWorldRenderConv);
    }

    protected IUserInterfaceProvider m_userInterface = null;

    protected OgreSceneMgr m_sceneMgr;

    protected Thread m_rendererThread = null;

    // If true, requesting a mesh causes the mesh to be rebuilt and written out
    // This makes sure cached copy is the same as server but is also slow
    protected bool m_shouldForceMeshRebuild = false;

    // this shouldn't be here... this is a feature of the LL renderer
    protected float m_sceneMagnification;
    public float SceneMagnification { get { return m_sceneMagnification; } }

    // we remember where the camera last was so we can do some interest management
    private OMV.Vector3d m_lastCameraPosition;

    protected BasicWorkQueue m_workQueue = new BasicWorkQueue("OgreRendererWork");
    protected OnDemandWorkQueue m_betweenFramesQueue = new OnDemandWorkQueue("OgreBetweenFrames");
    private static int m_betweenFrameTotalCost = 300;
    private static int m_betweenFrameMinTotalCost = 50;
    private static int m_betweenFrameCreateMaterialCost = 5;
    private static int m_betweenFrameCreateSceneNodeCost = 20;
    private static int m_betweenFrameCreateMeshCost = 20;
    private static int m_betweenFrameRefreshMeshCost = 20;
    private static int m_betweenFrameMapRegionCost = 50;
    private static int m_betweenFrameUpdateTerrainCost = 50;
    private static int m_betweenFrameMapTextureCost = 10;

    // private Thread m_rendererThread = null;
    private bool m_shouldRenderOnMainThread = false;

    private RestHandler m_restHandler;
    private StatisticManager m_stats;
    private ICounter m_statMaterialsRequested;
    private ICounter m_statMeshesRequested;
    private ICounter m_statTexturesRequested;

    private RestHandler m_ogreStatsHandler;
    private ParameterSet m_ogreStats;
    private int[] m_ogreStatsPinned;
    private GCHandle m_ogreStatsHandle;
    private Dictionary<string, int> m_ogreStatsIndex;
    private Dictionary<string, string> m_ogreStatsDesc;

    // ==========================================================================
    public RendererOgre() {
    }

    #region IModule
    public override void OnLoad(string name, LookingGlassBase lgbase) {
        base.OnLoad(name, lgbase);
        ModuleParams.AddDefaultParameter(m_moduleName + ".Ogre.InputSystem.Name", 
                    "OgreUI",
                    "Module to handle user IO on the rendering screen");

        ModuleParams.AddDefaultParameter(m_moduleName + ".Ogre.Name", "LookingGlass",
                    "Name of the Ogre resources to load");
        ModuleParams.AddDefaultParameter(m_moduleName + ".Ogre.SkyboxName", "LookingGlass/CloudyNoonSkyBox",
                    "Name of the skybox resource to use");
        ModuleParams.AddDefaultParameter(m_moduleName + ".Ogre.ShadowTechnique", "none",
                    "Shadow technique: none, texture-additive, texture-modulative, stencil-modulative, stencil-additive");
        ModuleParams.AddDefaultParameter(m_moduleName + ".Ogre.ShadowFarDistance", "100",
                    "Integer units of distance within which to do shadows (mul by magnification)");
        // cp.AddParameter(m_moduleName + ".Ogre.Renderer", "Direct3D9 Rendering Subsystem",
        //             "Name of the rendering subsystem to use");
        ModuleParams.AddDefaultParameter(m_moduleName + ".Ogre.Renderer", "OpenGL Rendering Subsystem",
                    "Name of the rendering subsystem to use");
        ModuleParams.AddDefaultParameter(m_moduleName + ".Ogre.VideoMode", "800 x 600@ 32-bit colour",
                    "Initial window size");
        ModuleParams.AddDefaultParameter(m_moduleName + ".Ogre.FramePerSecMax", "30",
                    "Maximum number of frames to display per second");
        ModuleParams.AddDefaultParameter(m_moduleName + ".ShouldRenderOnMainThread", "false",
                    "True if ogre rendering otherwise someone has to call RenderOneFrame");

        ModuleParams.AddDefaultParameter(m_moduleName + ".Ogre.PluginFilename", "plugins.cfg",
                    "File that lists Ogre plugins to load");
        ModuleParams.AddDefaultParameter(m_moduleName + ".Ogre.ResourcesFilename", "resources.cfg",
                    "File that lists the Ogre resources to load");
        ModuleParams.AddDefaultParameter(m_moduleName + ".Ogre.DefaultNumMipmaps", "2",
                    "Default number of mip maps created for a texture (usually 6)");
        ModuleParams.AddDefaultParameter(m_moduleName + ".Ogre.CacheDir", Utilities.GetDefaultApplicationStorageDir(null),
                    "Directory to store cached meshs, textures, etc");
        ModuleParams.AddDefaultParameter(m_moduleName + ".Ogre.PreLoadedDir", 
                    System.IO.Path.Combine(System.AppDomain.CurrentDomain.BaseDirectory, "./LookingGlassResources/Preloaded/"),
                    "Directory to for preloaded textures, etc");
        ModuleParams.AddDefaultParameter(m_moduleName + ".Ogre.DefaultTerrainMaterial",
                    "LookingGlass/DefaultTerrainMaterial",
                    "Material applied to terrain");
        ModuleParams.AddDefaultParameter(m_moduleName + ".Ogre.Ocean.Processor",
                    "none",
                    "The processing routine to create the ocean. Either 'none' or 'hydrax'");
        ModuleParams.AddDefaultParameter(m_moduleName + ".Ogre.OceanMaterialName",
                    "LookingGlass/Ocean",
                    "The ogre name of the ocean texture");
        ModuleParams.AddDefaultParameter(m_moduleName + ".Ogre.DefaultMeshFilename", 
                    System.IO.Path.Combine(System.AppDomain.CurrentDomain.BaseDirectory, "./LookingGlassResources/LoadingShape.mesh"),
                    "Filename of the default shape found in the cache dir");
        ModuleParams.AddDefaultParameter(m_moduleName + ".Ogre.DefaultTextureFilename", 
                    System.IO.Path.Combine(System.AppDomain.CurrentDomain.BaseDirectory, "./LookingGlassResources/LoadingTexture.png"),
                    "Filename of the default texture found in the cache dir");
        ModuleParams.AddDefaultParameter(m_moduleName + ".Ogre.DefaultTextureResourceName", 
                    "LoadingTexture.png",
                    "Resource name of  the default texture");

        ModuleParams.AddDefaultParameter(m_moduleName + ".Ogre.LL.SceneMagnification", "1",
                    "Magnification of LL coordinates into Ogre space");
        ModuleParams.AddDefaultParameter(m_moduleName + ".Ogre.LL.RenderInfoMaterialCreate", "true",
                    "Create materials while gathering mesh generation info (earlier than mesh creation)");
        ModuleParams.AddDefaultParameter(m_moduleName + ".Ogre.LL.EarlyMaterialCreate", "false",
                    "Create materials while creating mesh rather than waiting");

        ModuleParams.AddDefaultParameter(m_moduleName + ".Ogre.BetweenFrame.WorkItems", "1000",
                    "Cost of queued C++ work items to do between each frame");
        ModuleParams.AddDefaultParameter(m_moduleName + ".Ogre.BetweenFrame.Costs.Total", "1000",
                    "The total cost of C# operations to do between each frame");
        ModuleParams.AddDefaultParameter(m_moduleName + ".Ogre.BetweenFrame.Costs.CreateMaterial", "5",
                    "The cost of creating a material");
        ModuleParams.AddDefaultParameter(m_moduleName + ".Ogre.BetweenFrame.Costs.CreateSceneNode", "20",
                    "The cost of creating a scene node");
        ModuleParams.AddDefaultParameter(m_moduleName + ".Ogre.BetweenFrame.Costs.CreateMesh", "20",
                    "The cost of creating a mesh");
        ModuleParams.AddDefaultParameter(m_moduleName + ".Ogre.BetweenFrame.Costs.RefreshMesh", "20",
                    "The cost of refreshing a mesh (scanning all entities and reloading ones using mesh)");
        ModuleParams.AddDefaultParameter(m_moduleName + ".Ogre.BetweenFrame.Costs.MapRegion", "50",
                    "The cost of mapping a region (creating the region management structures");
        ModuleParams.AddDefaultParameter(m_moduleName + ".Ogre.BetweenFrame.Costs.UpdateTerrain", "50",
                    "The cost of updating the terrain (rebuilding terrain mesh)");
        ModuleParams.AddDefaultParameter(m_moduleName + ".Ogre.BetweenFrame.Costs.MapTexture", "10",
                    "The cost of mapping a texture (doing a texture reload)");

        ModuleParams.AddDefaultParameter(m_moduleName + ".Ogre.SerializeMaterials", "false",
                    "Write out materials to files (replace with DB someday)");
        ModuleParams.AddDefaultParameter(m_moduleName + ".Ogre.SerializeMeshes", "true",
                    "Write out meshes to files");
        ModuleParams.AddDefaultParameter(m_moduleName + ".Ogre.ForceMeshRebuild", "false",
                    "True if to force the generation a mesh when first rendered (don't rely on cache)");

        ModuleParams.AddDefaultParameter(m_moduleName + ".Ogre.Sky", "Default",
                    "Name of the key system to use");
        ModuleParams.AddDefaultParameter(m_moduleName + ".Ogre.SkyX.LightingHDR", "true",
                    "Use high resolution lighting shaders");

        ModuleParams.AddDefaultParameter(m_moduleName + ".Ogre.Ambient", "<0.4,0.4,0.4>",
                    "color value for initial ambient lighting");
        ModuleParams.AddDefaultParameter(m_moduleName + ".Ogre.Sun.Color", "<1.0,1.0,1.0>",
                    "Color of light from the sun at noon");
        ModuleParams.AddDefaultParameter(m_moduleName + ".Ogre.Moon.Color", "<0.5,0.5,0.6>",
                    "Color of light from the moon");

        ModuleParams.AddDefaultParameter(m_moduleName + ".Ogre.Visibility.Processor", "FrustrumDistance",
                    "Name of the culling plugin to run ('FrustrumDistance' is only one)");
        ModuleParams.AddDefaultParameter(m_moduleName + ".Ogre.Visibility.Cull.Frustrum", "true",
                    "whether to cull (unload) objects if not visible in camera frustrum");
        ModuleParams.AddDefaultParameter(m_moduleName + ".Ogre.Visibility.Cull.Distance", "true",
                    "whether to cull (unload) objects depending on distance from camera");
        ModuleParams.AddDefaultParameter(m_moduleName + ".Ogre.Visibility.Cull.Meshes", "true",
                    "unload culled object meshes");
        ModuleParams.AddDefaultParameter(m_moduleName + ".Ogre.Visibility.Cull.Textures", "true",
                    "unload culled textures");
        ModuleParams.AddDefaultParameter(m_moduleName + ".Ogre.Visibility.MaxDistance", "200",
                    "the maximum distance to see any entites (far clip)");
        ModuleParams.AddDefaultParameter(m_moduleName + ".Ogre.Visibility.MinDistance", "30",
                    "below this distance, everything is visible");
        ModuleParams.AddDefaultParameter(m_moduleName + ".Ogre.Visibility.OnlyLargeAfter", "120",
                    "After this distance, only large things are visible");
        ModuleParams.AddDefaultParameter(m_moduleName + ".Ogre.Visibility.Large", "8",
                    "How big is considered 'large' for 'OnlyLargeAfter' calculation");
        ModuleParams.AddDefaultParameter(m_moduleName + ".Ogre.Visibility.MeshesReloadedPerFrame", "50",
                    "When reloading newly visible meshes, how many to load per frame");

        // some counters and intervals to see how long things take
        m_stats = new StatisticManager(m_moduleName);
        m_statMaterialsRequested = m_stats.GetCounter("MaterialsRequested");
        m_statMeshesRequested = m_stats.GetCounter("MeshesRequested");
        m_statTexturesRequested = m_stats.GetCounter("TexturesRequested");

        // renderer keeps rendering specific data in an entity's addition/subsystem slots
        AddSceneNodeName = EntityBase.AddAdditionSubsystem(RendererOgre.AddSceneNodeNameName);
        AddRegionSceneNode = EntityBase.AddAdditionSubsystem(RendererOgre.AddRegionSceneNodeName);
        AddTerrainSceneNode = EntityBase.AddAdditionSubsystem(RendererOgre.AddTerrainSceneNodeName);
        AddWorldRenderConv = EntityBase.AddAdditionSubsystem(RendererOgre.AddWorldRenderConvName);
    }

    // these exist to keep one managed pointer to the delegates so they don't get garbage collected
    private Ogr.DebugLogCallback debugLogCallbackHandle;
    private Ogr.FetchParameterCallback fetchParameterCallbackHandle;
    private Ogr.CheckKeepRunningCallback checkKeepRunningCallbackHandle;
    private Ogr.UserIOCallback userIOCallbackHandle;
    private Ogr.RequestResourceCallback requestResourceCallbackHandle;
    private Ogr.BetweenFramesCallback betweenFramesCallbackHandle;
    // ==========================================================================
    override public bool AfterAllModulesLoaded() {
        // allow others to get our statistics
        m_restHandler = new RestHandler("/stats/" + m_moduleName + "/detailStats", m_stats);

        #region OGRE STATS
        // Setup the shared piece of memory that Ogre can place statistics in
        m_ogreStatsPinned = new int[Ogr.StatSize];
        m_ogreStatsHandle = GCHandle.Alloc(m_ogreStatsPinned, GCHandleType.Pinned);
        Ogr.SetStatsBlock(m_ogreStatsHandle.AddrOfPinnedObject());
        // m_ogreStatsPinned = (int[])Marshal.AllocHGlobal(Ogr.StatSize * 4);
        // Ogr.SetStatsBlock(m_ogreStatsPinned);

        // Create a ParameterSet that can be read externally via REST/JSON
        m_ogreStats = new ParameterSet();
        // add an initial parameter that calculates frames per sec
        m_ogreStats.Add("FramesPerSecond",
            delegate(string xx) {
                return new OMVSD.OSDString(((float)m_ogreStatsPinned[Ogr.StatFramesPerSec] / 1000f).ToString());
            }, "Frames per second"
        );
        m_ogreStats.Add("LastFrameMS", delegate(string xx) {
                return new OMVSD.OSDString(m_ogreStatsPinned[Ogr.StatLastFrameMs].ToString()); },
                "Milliseconds used rendering last frame");
        m_ogreStats.Add("TotalFrames", delegate(string xx) {
                return new OMVSD.OSDString(m_ogreStatsPinned[Ogr.StatTotalFrames].ToString()); },
                "Number of frames rendered");
        m_ogreStats.Add("VisibleToVisible", delegate(string xx) {
                return new OMVSD.OSDString(m_ogreStatsPinned[Ogr.StatVisibleToVisible].ToString()); },
                "Meshes at were visible that are still visible in last frame");
        m_ogreStats.Add("InvisibleToVisible", delegate(string xx) {
                return new OMVSD.OSDString(m_ogreStatsPinned[Ogr.StatInvisibleToVisible].ToString()); },
                "Meshes that were invisible that are now visible in last frame");
        m_ogreStats.Add("VisibleToInvisible", delegate(string xx) {
                return new OMVSD.OSDString(m_ogreStatsPinned[Ogr.StatVisibleToInvisible].ToString()); },
                "Meshes that were visible that are now invisible in last frame");
        m_ogreStats.Add("InvisibleToInvisible", delegate(string xx) {
                return new OMVSD.OSDString(m_ogreStatsPinned[Ogr.StatInvisibleToInvisible].ToString()); },
                "Meshes that were invisible that are still invisible in last frame");
        m_ogreStats.Add("CullMeshesLoaded", delegate(string xx) {
                return new OMVSD.OSDString(m_ogreStatsPinned[Ogr.StatCullMeshesLoaded].ToString()); },
                "Total meshes loaded due to unculling");
        m_ogreStats.Add("CullTexturesLoaded", delegate(string xx) {
                return new OMVSD.OSDString(m_ogreStatsPinned[Ogr.StatCullTexturesLoaded].ToString()); },
                "Total textures loaded due to unculling");
        m_ogreStats.Add("CullMeshesUnloaded", delegate(string xx) {
                return new OMVSD.OSDString(m_ogreStatsPinned[Ogr.StatCullMeshesUnloaded].ToString()); },
                "Total meshes unloaded due to culling");
        m_ogreStats.Add("CullTexturesUnloaded", delegate(string xx) {
                return new OMVSD.OSDString(m_ogreStatsPinned[Ogr.StatCullTexturesUnloaded].ToString()); },
                "Total textures unloaded due to culling");
        m_ogreStats.Add("CullMeshesQueuedToLoad", delegate(string xx) {
                return new OMVSD.OSDString(m_ogreStatsPinned[Ogr.StatCullMeshesQueuedToLoad].ToString()); },
                "Meshes currently queued to load due to unculling");
        // between frame work
        m_ogreStats.Add("BetweenFrameworkItems", delegate(string xx) {
                return new OMVSD.OSDString(m_ogreStatsPinned[Ogr.StatBetweenFrameWorkItems].ToString()); },
                "Number of between frame work items waiting");
        m_ogreStats.Add("TotalBetweenFrameRefreshResource", delegate(string xx) {
                return new OMVSD.OSDString(m_ogreStatsPinned[Ogr.StatBetweenFrameRefreshResource].ToString()); },
                "Number of 'refresh resource' work items queued");
        m_ogreStats.Add("TotalBetweenFrameCreateMaterialResource", delegate(string xx) {
                return new OMVSD.OSDString(m_ogreStatsPinned[Ogr.StatBetweenFrameCreateMaterialResource].ToString()); },
                "Number of 'create material resource' work items queued");
        m_ogreStats.Add("TotalBetweenFrameCreateMeshResource", delegate(string xx) {
                return new OMVSD.OSDString(m_ogreStatsPinned[Ogr.StatBetweenFrameCreateMeshResource].ToString()); },
                "Number of 'create mesh resource' work items queued");
        m_ogreStats.Add("TotalBetweenFrameCreateMeshScenenode", delegate(string xx) {
                return new OMVSD.OSDString(m_ogreStatsPinned[Ogr.StatBetweenFrameCreateMeshSceneNode].ToString()); },
                "Number of 'create mesh scene node' work items queued");
        m_ogreStats.Add("TotalBetweenframeupdatescenenode", delegate(string xx) {
                return new OMVSD.OSDString(m_ogreStatsPinned[Ogr.StatBetweenFrameUpdateSceneNode].ToString()); },
                "Number of 'update scene node' work items queued");
        m_ogreStats.Add("TotalBetweenFrameUnknownProcess", delegate(string xx) {
                return new OMVSD.OSDString(m_ogreStatsPinned[Ogr.StatBetweenFrameUnknownProcess].ToString()); },
                "Number of work items with unknow process codes");
        m_ogreStats.Add("TotalBetweenFrameTotalProcessed", delegate(string xx) {
                return new OMVSD.OSDString(m_ogreStatsPinned[Ogr.StatBetweenFrameTotalProcessed].ToString()); },
                "Total number of work items actually processed");
        // material processing queues
        m_ogreStats.Add("MaterialUpdatesRemaining", delegate(string xx) {
                return new OMVSD.OSDString(m_ogreStatsPinned[Ogr.StatMaterialUpdatesRemaining].ToString()); },
                "Number of material updates waiting");

        // make the values accessable from outside
        m_ogreStatsHandler = new RestHandler("/stats/" + m_moduleName + "/ogreStats", m_ogreStats);
        #endregion OGRE STATS

        // Load the input system we're supposed to be using
        // The input system is a module tha we get given the name of. Go find it and link it in.
        String uiModule = ModuleParams.ParamString(m_moduleName + ".Ogre.InputSystem.Name");
        if (uiModule != null && uiModule.Length > 0) {
            try {
                m_log.Log(LogLevel.DRENDER, "Loading UI processor '{0}'", uiModule);
                m_userInterface = (IUserInterfaceProvider)LGB.ModManager.Module(uiModule);
                if (m_userInterface == null) {
                    m_log.Log(LogLevel.DBADERROR, "FATAL: Could not find user interface class {0}", uiModule);
                    return false;
                }
            }
            catch (Exception e) {
                m_log.Log(LogLevel.DBADERROR, "FATAL: Could not load user interface class {0}: {1}", uiModule, e.ToString());
                return false;
            }
        }
        else {
            m_log.Log(LogLevel.DBADERROR, "Using null user interfare");
            m_userInterface = new UserInterfaceNull();
        }

        // if we are doing detail logging, enable logging by  the LookingGlassOgre code
        if (m_log.WouldLog(LogLevel.DRENDERDETAIL)) {
            m_log.Log(LogLevel.DRENDER, "Logging detail high enough to enable unmanaged code log messages");
            debugLogCallbackHandle = new Ogr.DebugLogCallback(OgrLogger);
            Ogr.SetDebugLogCallback(debugLogCallbackHandle);
        }
        // push the callback pointers into the LookingGlassOgre code
        fetchParameterCallbackHandle = new Ogr.FetchParameterCallback(GetAParameter);
        Ogr.SetFetchParameterCallback(fetchParameterCallbackHandle);
        checkKeepRunningCallbackHandle = new Ogr.CheckKeepRunningCallback(CheckKeepRunning);
        Ogr.SetCheckKeepRunningCallback(checkKeepRunningCallbackHandle);

        // link the input devices to and turn on low level IO reception
        if (m_userInterface.NeedsRendererLinkage()) {
            userIOCallbackHandle = new Ogr.UserIOCallback(ReceiveUserIOConv);
            Ogr.SetUserIOCallback(userIOCallbackHandle);
        }

        // handles so unmanaged code can call back to managed code
        requestResourceCallbackHandle = new Ogr.RequestResourceCallback(RequestResource);
        Ogr.SetRequestResourceCallback(requestResourceCallbackHandle);
        betweenFramesCallbackHandle = new Ogr.BetweenFramesCallback(ProcessBetweenFrames);
        Ogr.SetBetweenFramesCallback(betweenFramesCallbackHandle);

        m_sceneMagnification = float.Parse(ModuleParams.ParamString(m_moduleName + ".Ogre.LL.SceneMagnification"));

        m_shouldForceMeshRebuild = ModuleParams.ParamBool(m_moduleName + ".Ogre.ForceMeshRebuild");
        m_shouldRenderOnMainThread = ModuleParams.ParamBool(m_moduleName + ".ShouldRenderOnMainThread");

        // pick up a bunch of parameterized values
        m_betweenFrameTotalCost = ModuleParams.ParamInt(m_moduleName + ".Ogre.BetweenFrame.Costs.Total");
        m_betweenFrameCreateMaterialCost = ModuleParams.ParamInt(m_moduleName + ".Ogre.BetweenFrame.Costs.CreateMaterial");
        m_betweenFrameCreateSceneNodeCost = ModuleParams.ParamInt(m_moduleName + ".Ogre.BetweenFrame.Costs.CreateSceneNode");
        m_betweenFrameCreateMeshCost = ModuleParams.ParamInt(m_moduleName + ".Ogre.BetweenFrame.Costs.CreateMesh");
        m_betweenFrameRefreshMeshCost = ModuleParams.ParamInt(m_moduleName + ".Ogre.BetweenFrame.Costs.RefreshMesh");
        m_betweenFrameMapRegionCost = ModuleParams.ParamInt(m_moduleName + ".Ogre.BetweenFrame.Costs.MapRegion");
        m_betweenFrameUpdateTerrainCost = ModuleParams.ParamInt(m_moduleName + ".Ogre.BetweenFrame.Costs.UpdateTerrain");
        m_betweenFrameMapTextureCost = ModuleParams.ParamInt(m_moduleName + ".Ogre.BetweenFrame.Costs.MapTexture");

        // start up the Ogre renderer
        try {
            Ogr.InitializeOgre();
        }
        catch (Exception e) {
            m_log.Log(LogLevel.DBADERROR, "EXCEPTION INITIALIZING OGRE: {0}", e.ToString());
            return false;
        }
        m_sceneMgr = new OgreSceneMgr(Ogr.GetSceneMgr());

        // if we get here, rendering is set up and running
        return true;
    }

    // do some type conversion of the stuff coming in from the unmanaged code
    private void ReceiveUserIOConv(int type, int param1, float param2, float param3) {
        m_userInterface.ReceiveUserIO((ReceiveUserIOInputEventTypeCode)type, param1, param2, param3);
        return;
    }

    // routine called from unmanaged code to log a message
    private void OgrLogger(string msg) {
        m_logOgre.Log(LogLevel.DRENDERDETAIL, msg);
    }
    
    // Called from unmanaged code to get the state of the KeepRunning flag
    // Not used much since the between frame callback also returns the KeepRunning flag
    private bool CheckKeepRunning() {
        return LGB.KeepRunning;
    }

    // Called from unmanaged code to get the value of a parameter
    // Fixed so this doesn't throw but returns a null value. Calling code has to check return value.
    private string GetAParameter(string parm) {
        string ret = null;
        paramErrorType oldtype = ModuleParams.ParamErrorMethod;
        ModuleParams.ParamErrorMethod = paramErrorType.eException;
        try {
            ret = ModuleParams.ParamString(parm);
        }
        catch {
            m_log.Log(LogLevel.DBADERROR, "GetAParameter: returning default value for parameter {0}", parm);
            ret = "";
        }
        ModuleParams.ParamErrorMethod = oldtype;
        return ret;
    }

    // ==========================================================================
    // IModule.Start()
    override public void Start() {
        if (m_shouldRenderOnMainThread) {
            m_log.Log(LogLevel.DRENDER, "Start: requesting main thread");
            LGB.GetMainThread(RendererThread);
        }
        return;
    }

    // ==========================================================================
    // IModule.Stop()
    override public void Stop() {
        return;
    }

    // ==========================================================================
    // IModule.PrepareForUnload()
    override public bool PrepareForUnload() {
        return false;
    }
    #endregion IModule

    #region IRenderProvider
    public IUserInterfaceProvider UserInterface {
        get { return m_userInterface; }
    }

    // Given the main thread. Run and then say we're all done.
    public bool RendererThread() {
        m_log.Log(LogLevel.DRENDER, "RendererThread: have main thread");
        /*
        // Try creating a thread just for the renderer
        // create a thread for the renderer
        // NOTE: this does not work as the display thread needs to be the main window thread
        m_rendererThread = new Thread(RunRenderer);
        m_rendererThread.Name = "Renderer";
        m_rendererThread.Start();
        // tell the caller I'm not using the main thread
        return false;
         */

        // Let the Ogre code decide if it needs the main thread
        return Ogr.RenderingThread();

        /*
        // HISTORICAL NOTE: origionally this was done here in managed space
        //  but all the between frame work (except terrain) was moved into unmanaged code
        //  This code should be deleted someday.
        // code  to keep the rendering thread mostly in managed space
        // periodically call to draw a frame
        int maxFramePerSec = ModuleParams.ParamInt(m_moduleName + ".Ogre.FramePerSecMax");
        int msPerFrame = 1000 / maxFramePerSec;
        int frameStart, frameEnd, frameDuration, frameLeft;
        while (LGB.KeepRunning) {
            frameStart = System.Environment.TickCount;
            if (!Ogr.RenderOneFrame(true, 100)) {
                LGB.KeepRunning = false;
            }
            while (true) {
                frameEnd = System.Environment.TickCount;
                frameDuration = frameEnd - frameStart;
                frameLeft = msPerFrame - frameDuration;
                if (frameLeft < 10) break;
                if (IsProcessBetweenFramesWork()) {
                    ProcessBetweenFrames(m_betweenFrameMinTotalCost);
                }
                else {
                    Thread.Sleep(frameLeft);
                    break;
                }
            }
        }
        return false;
         */
    }

    public bool RenderOneFrame(bool pump, int len) {
        return Ogr.RenderOneFrame(pump, len);
    }

    // pass the thread into the renderer
    private void RunRenderer() {
        Ogr.RenderingThread();
        return;
    }

    // ==========================================================================
    // IRenderProvider.Render()
    public void Render(IEntity ent) {
        // do we have a format converted for this entity?
        if (RendererOgre.GetWorldRenderConv(ent) == null) {
            // for the moment, we only know about LL stuff
            // TODO: Figure out how to make this dynamic, extendable and runtime
            ent.SetAddition(RendererOgre.AddWorldRenderConv, RendererOgreLL.Instance);
        }
        DoRenderQueued(ent);
        return;
    }

    // wrapper routine for the queuing if rendering work for this entity
    private void DoRenderQueued(IEntity ent) {
        Object[] renderParameters = { ent, null };
        m_workQueue.DoLater(CalculateInterestOrder(ent), DoRenderLater, renderParameters);
    }

    private bool DoRenderLater(DoLaterBase qInstance, Object parms) {
        Object[] loadParams = (Object[])parms;
        IEntity m_ent = (IEntity)loadParams[0];
        RenderableInfo m_ri = (RenderableInfo)loadParams[1];
        string entitySceneNodeName = EntityNameOgre.ConvertToOgreSceneNodeName(m_ent.Name);

        lock (m_ent) {
            if (RendererOgre.GetSceneNodeName(m_ent) == null) {
                try {
                    // m_log.Log(LogLevel.DRENDERDETAIL, "Adding SceneNode to new entity " + m_ent.Name);
                    if (m_ri == null) {
                        m_ri = RendererOgre.GetWorldRenderConv(m_ent).RenderingInfo(qInstance.priority,
                                m_sceneMgr, m_ent, qInstance.timesRequeued);
                        if (m_ri == null) {
                            // The rendering info couldn't be built now. This is usually because
                            // the parent of this object is not available so we don't know where to put it
                            m_log.Log(LogLevel.DRENDERDETAIL,
                                "Delaying rendering {0}/{1}. RenderingInfo not built for {2}",
                                qInstance.sequence, qInstance.timesRequeued, m_ent.Name.Name);
                            return false;
                        }
                        else {
                            // save the value in the parameter block if we get called again ('return false' below)
                            loadParams[1] = (Object)m_ri;
                        }
                    }

                    // Find a handle to the parent for this node
                    string parentSceneNodeName = null;
                    if (m_ri.parentEntity != null) {
                        // this entity has a parent entity. create scene node off his
                        IEntity parentEnt = m_ri.parentEntity;
                        parentSceneNodeName = EntityNameOgre.ConvertToOgreSceneNodeName(parentEnt.Name);
                    }
                    else {
                        parentSceneNodeName = EntityNameOgre.ConvertToOgreSceneNodeName(m_ent.RegionContext.Name);
                    }

                    // Create the scene node for this entity
                    // and add the definition for the object on to the scene node
                    // This will cause the load function to be called and create all
                    //   the callbacks that will actually create the object
                    string entMeshName = (string)m_ri.basicObject;
                    if (!m_sceneMgr.CreateMeshSceneNodeBF(qInstance.priority,
                                    entitySceneNodeName,
                                    parentSceneNodeName,
                                    entMeshName,
                                    false, true,
                                    m_ri.position.X, m_ri.position.Y, m_ri.position.Z,
                                    m_ri.scale.X, m_ri.scale.Y, m_ri.scale.Z,
                                    m_ri.rotation.W, m_ri.rotation.X, m_ri.rotation.Y, m_ri.rotation.Z)) {
                        m_log.Log(LogLevel.DRENDERDETAIL, "Delaying rendering {0}/{1}. {2} waiting for parent {3}",
                            qInstance.sequence, qInstance.timesRequeued, m_ent.Name.Name,
                            (parentSceneNodeName == null ? "NULL" : parentSceneNodeName));
                        return false;   // if I must have parent, requeue if no parent
                    }

                    // Add the name of the created scene node name so we know it's created and
                    // we can find it later.
                    m_ent.SetAddition(RendererOgre.AddSceneNodeName, entitySceneNodeName);

                    // Experiemental: force the creation of the mesh
                    if (m_shouldForceMeshRebuild) {
                        RequestMesh(m_ent.Name.Name, entMeshName);
                    }
                }
                catch (Exception e) {
                    m_log.Log(LogLevel.DBADERROR, "Render: Failed conversion: " + e.ToString());
                }
            }
            else {
                // if this has already been processed, just return success
                return true;
            }
        }

        return true;
    }

    /// <summary>
    /// How interesting is this entity. Returns a floating point number that ranges from
    /// zero (VERY interested) to N (less interested). The rough calculation is the distance
    /// from the agent but it need not be linear (things behind can be 'farther away'.
    /// </summary>
    /// <param name="ent">Entity we're checking interest in</param>
    /// <returns>Zero to N with larger meaning less interested</returns>
    private int CalculateInterestOrder(IEntity ent) {
        float ret = 100f;
        if (m_lastCameraPosition != null) {
            double dist = OMV.Vector3d.Distance(ent.GlobalPosition, m_lastCameraPosition);
            if (dist < 0) dist = -dist;
            if (dist > 1000.0) dist = 1000.0;
            ret = (float)dist;
        }
        return (int)ret;
    }

    // ==========================================================================
    public void RenderUpdate(IEntity ent, UpdateCodes what) {
        if ((what & UpdateCodes.Material) != 0) {
            // the materials have changed on this entity. Cause materials to be recalcuated
            m_log.Log(LogLevel.DRENDERDETAIL, "RenderUpdate: Material changed");
        }
        if ((what & (UpdateCodes.Scale | UpdateCodes.Position | UpdateCodes.Rotation)) != 0) {
            // world position has changed. Tell Ogre they have changed
            string entitySceneNodeName = EntityNameOgre.ConvertToOgreSceneNodeName(ent.Name);
            m_log.Log(LogLevel.DRENDERDETAIL, "RenderUpdate: Updating position/rotation for {0}", entitySceneNodeName);
            Ogr.UpdateSceneNodeBF(CalculateInterestOrder(ent), entitySceneNodeName,
                ((what & UpdateCodes.Position) != 0),
                ent.RelativePosition.X, ent.RelativePosition.Y, ent.RelativePosition.Z,
                false, 1f, 1f, 1f,  // don't pass scale yet
                ((what & UpdateCodes.Rotation) != 0),
                ent.Heading.W, ent.Heading.X, ent.Heading.Y, ent.Heading.Z);
        }
        if ((what & UpdateCodes.ParentID) != 0) {
            // prim was detached or attached
            m_log.Log(LogLevel.DRENDERDETAIL, "RenderUpdate: parentID changed");
            DoRenderQueued(ent);
        }
        if ((what & (UpdateCodes.PrimFlags | UpdateCodes.PrimData)) != 0) {
            // the prim parameters were changed. Re-render.
            m_log.Log(LogLevel.DRENDERDETAIL, "RenderUpdate: prim data changed");
            DoRenderQueued(ent);
        }
        if ((what & UpdateCodes.Textures) != 0) {
            // texure on the prim were updated. Refresh them.
            m_log.Log(LogLevel.DRENDERDETAIL, "RenderUpdate: textures changed");
            DoRenderQueued(ent);
        }
        if ((what & UpdateCodes.Text) != 0) {
            // text associated with the prim changed
            m_log.Log(LogLevel.DRENDERDETAIL, "RenderUpdate: text changed");
        }
        if ((what & UpdateCodes.Particles) != 0) {
            // particles associated with the prim changed
            m_log.Log(LogLevel.DRENDERDETAIL, "RenderUpdate: particles changed");
        }
        return;
    }

    // ==========================================================================
    public void UnRender(IEntity ent) {
        return;
    }

    // ==========================================================================
    /// <summary>
    /// Update the camera. The coordinate system from the EntityCamera is LL's
    /// (Z up). We have to convert the rotation and position to Ogre coords
    /// (Y up).
    /// </summary>
    /// <param name="cam"></param>
    public void UpdateCamera(CameraControl cam) {
        // OMV.Quaternion orient = new OMV.Quaternion(OMV.Vector3.UnitX, -Constants.PI / 2)
                    // * new OMV.Quaternion(OMV.Vector3.UnitZ, -Constants.PI / 2)
                    // * cam.Direction;
        // we need to rotate the camera 90 to make it work out in Ogre. Not sure why.
        // OMV.Quaternion orient = cam.Heading * OMV.Quaternion.CreateFromAxisAngle(OMV.Vector3.UnitZ, -Constants.PI / 2);
        OMV.Quaternion orient = OMV.Quaternion.CreateFromAxisAngle(OMV.Vector3.UnitZ, -Constants.PI / 2) * cam.Heading;
        // OMV.Quaternion orient = cam.Heading;
        orient.Normalize();
        OMV.Vector3d pos = cam.GlobalPosition * m_sceneMagnification;
        m_lastCameraPosition = cam.GlobalPosition;
        Ogr.UpdateCamera((float)pos.X, (float)pos.Z, (float)-pos.Y, 
            orient.W, orient.X, orient.Z, -orient.Y,
            1.0f, (float)cam.Far*m_sceneMagnification, 1.0f);
        // m_log.Log(LogLevel.DRENDERDETAIL, "UpdateCamera: Camera to {0}, {1}, {2}",
        //     (float)pos.X, (float)pos.Z, (float)-pos.Y);
        return;
    }

    // ==========================================================================
    public void UpdateEnvironmentalLights(EntityLight sunLight, EntityLight moonLight) {
        return;
    }
    
    // ==========================================================================
    // Given the current input pointer, return a point in the world
    public OMV.Vector3d SelectPoint() {
        return new OMV.Vector3d(0, 0, 0);
    }
    
    // ==========================================================================
    public void MapRegionIntoView(RegionContextBase rcontext) {
        lock (rcontext) {
            // see that the region entity has a converter between world and renderer
            // TODO: Figure out how to make this dynamic, extendable and runtime
            if (rcontext.WorldGroup == World.WorldGroupCode.LLWorld) {
                if (RendererOgre.GetWorldRenderConv(rcontext) == null) {
                    rcontext.SetAddition(RendererOgre.AddWorldRenderConv, new RendererOgreLL());
                }
            }
        }
        m_betweenFramesQueue.DoLater(new MapRegionLater(m_sceneMgr, rcontext, m_log));
        return;
    }

    private sealed class MapRegionLater : DoLaterBase {
        OgreSceneMgr m_sceneMgr;
        RegionContextBase m_rcontext;
        ILog m_log;
        public MapRegionLater(OgreSceneMgr smgr, RegionContextBase rcon, ILog logr) : base() {
            m_sceneMgr = smgr;
            m_rcontext = rcon;
            m_log = logr;
            this.cost = m_betweenFrameMapRegionCost;
        }
        override public bool DoIt() {
            RendererOgre.GetWorldRenderConv(m_rcontext).MapRegionIntoView(this.priority, m_sceneMgr, m_rcontext);
            return true;
        }
    }
    
    // ==========================================================================
    public RenderableInfo RenderingInfo(IEntity ent) {
        return null;
    }

    // ==========================================================================
    public void UpdateTerrain(RegionContextBase rcontext) {
        m_betweenFramesQueue.DoLater(new UpdateTerrainLater(this, m_sceneMgr, rcontext, m_log));
        return;
    }
    private sealed class UpdateTerrainLater : DoLaterBase {
        RendererOgre m_renderer;
        OgreSceneMgr m_sceneMgr;
        RegionContextBase m_rcontext;
        ILog m_log;
        public UpdateTerrainLater(RendererOgre renderer, OgreSceneMgr smgr, RegionContextBase rcontext, ILog logr) : base() {
            m_renderer = renderer;
            m_sceneMgr = smgr;
            m_rcontext = rcontext;
            m_log = logr;
            this.cost = m_betweenFrameUpdateTerrainCost;
        }
        override public bool DoIt() {
            OgreSceneNode sn;
            // see if there is a terrain SceneNode already allocated
            if (RendererOgre.GetTerrainSceneNode(m_rcontext) != null) {
                sn = RendererOgre.GetTerrainSceneNode(m_rcontext);
            }
            else {
                OgreSceneNode regionSceneNode = RendererOgre.GetRegionSceneNode(m_rcontext);
                // no existing scene node, create one
                if (regionSceneNode == null) {
                    // we have to wait until there is a region root before placing texture
                    m_log.Log(LogLevel.DRENDERDETAIL, "RendererOgre: UpdateTerrain: waiting for region root");
                    return false;
                }
                else {
                    // m_log.Log(LogLevel.DRENDERDETAIL, "RenderOgre: UpdateTerrain: Using world specific root node");
                    string terrainNodeName = "Terrain/" + m_rcontext.Name + "/" + OMV.UUID.Random().ToString();
                    sn = m_sceneMgr.CreateSceneNode(terrainNodeName, regionSceneNode, 
                                false, true, 
                                // the terrain is attached to the region node so it's at relative address
                                0f, 0f, 0f,
                                // scaling matches the LL to Ogre map
                                m_renderer.SceneMagnification, m_renderer.SceneMagnification, m_renderer.SceneMagnification,
                                OMV.Quaternion.Identity.W, OMV.Quaternion.Identity.X,
                                OMV.Quaternion.Identity.Y, OMV.Quaternion.Identity.Z
                    );
                    m_rcontext.SetAddition(RendererOgre.AddTerrainSceneNode, sn);
                    m_log.Log(LogLevel.DRENDERDETAIL, "Creating terrain " + sn.Name);
                }
            }

            try {
                float[,] hm = m_rcontext.TerrainInfo.HeightMap;
                int hmWidth = m_rcontext.TerrainInfo.HeightMapWidth;
                int hmLength = m_rcontext.TerrainInfo.HeightMapLength;

                int loc = 0;
                float[] passingHM = new float[hmWidth * hmLength];
                for (int xx = 0; xx < hmWidth; xx++) {
                    for (int yy = 0; yy < hmLength; yy++) {
                        passingHM[loc++] = hm[xx, yy];
                    }
                }

                Ogr.GenTerrainMesh(m_sceneMgr.BasePtr, sn.BasePtr, hmWidth, hmLength, passingHM);
            }
            catch (Exception e) {
                m_log.Log(LogLevel.DBADERROR, "UpdateTerrainLater: mesh creation failure: " + e.ToString());
            }

            return true;
        }
    }
    #endregion IRenderProvider

    // ==========================================================================
    private void RequestResource(string resourceContext, string resourceName, int resourceType) {
        switch (resourceType) {
            case Ogr.ResourceTypeMesh:
                m_statMeshesRequested.Event();
                RequestMesh(resourceContext, resourceName);
                break;
            case Ogr.ResourceTypeMaterial:
                m_statMaterialsRequested.Event();
                RequestMaterial(resourceContext, resourceName);
                // RequestMaterialX(resourceContext, resourceName);
                break;
            case Ogr.ResourceTypeTexture:
                m_statTexturesRequested.Event();
                RequestTexture(resourceContext, resourceName);
                break;
        }
        return;
    }

    public Dictionary<string, string> MeshesWaiting = new Dictionary<string, string>();
    private void RequestMesh(string contextEntity, string meshName) {
        m_log.Log(LogLevel.DRENDERDETAIL, "Request for mesh " + meshName);
        lock (MeshesWaiting) {
            // if already working on this mesh, don't do it again
            if (MeshesWaiting.ContainsKey(meshName)) return;
            MeshesWaiting.Add(meshName, contextEntity);
        }
        m_workQueue.DoLater(RequestMeshLater, (object)meshName);
        return;
    }

    // Called on workQueue to call into gather parameters and create the mesh resource
    private bool RequestMeshLater(DoLaterBase qInstance, Object parm) {
        string m_meshName = (string)parm;
        try {
            // type information is at the end of the name. remove if there
            EntityName eName = EntityNameOgre.ConvertOgreResourceToEntityName(m_meshName);
            // the terrible kludge here is that the name of the mesh is the name of
            //   the entity. We reach back into the worlds and find the underlying
            //   entity then we can construct the mesh.
            IEntity ent;
            if (!World.World.Instance.TryGetEntity(eName, out ent)) {
                m_log.Log(LogLevel.DBADERROR, "RendererOgre.RequestMeshLater: could not find entity " + eName);
                return true;
            }
            // Create mesh resource. In this case its most likely a .mesh file in the cache
            // The actual mesh creation is queued and done later between frames
            int priority = CalculateInterestOrder(ent);
            if (!RendererOgre.GetWorldRenderConv(ent).CreateMeshResource(priority, ent, m_meshName)) {
                // we need to wait until some resource exists before we can complete this creation
                return false;
            }

            // tell Ogre to refresh (reload) the resource
            m_log.Log(LogLevel.DRENDERDETAIL, "RendererOgre.RequestMeshLater: refresh for {0}", m_meshName);
            Ogr.RefreshResourceBF(priority, Ogr.ResourceTypeMesh, m_meshName);
            lock (this.MeshesWaiting) {
                // no longer waiting for this mesh to get created
                if (this.MeshesWaiting.ContainsKey(m_meshName)) {
                    this.MeshesWaiting.Remove(m_meshName);
                }
            }
        }
        catch {
            // an oddity but not fatal
        }
        return true;
    }
    
    /// <summary>
    /// Request from the C++ world to create a specific material resource. We queue
    /// the request on a work queue so the renderer can get back to work.
    /// The material information is gathered and then sent back to the renderer.
    /// </summary>
    /// <param name="contextEntity"></param>
    /// <param name="matName"></param>
    private void RequestMaterial(string contextEntity, string matName) {
        m_log.Log(LogLevel.DRENDERDETAIL, "Request for material " + matName);
        EntityNameOgre entName = EntityNameOgre.ConvertOgreResourceToEntityName(contextEntity);
        // m_workQueue.DoLater(new RequestMaterialDoLater(m_sceneMgr, entName, matName));
        Object[] materialParameters = { entName, matName };
        m_workQueue.DoLater(RequestMaterialLater, materialParameters);

        return;
    }

    // Called on workqueue thread to create material in Ogre
    private bool RequestMaterialLater(DoLaterBase qInstance, Object parms) {
        Object[] loadParams = (Object[])parms;
        EntityNameOgre m_entName = (EntityNameOgre)loadParams[0];
        string m_matName = (string)loadParams[1];
        try {
            IEntity ent;
            if (World.World.Instance.TryGetEntity(m_entName, out ent)) {
                // LogManager.Log.Log(LogLevel.DRENDERDETAIL, "RequestMaterialLater.DoIt(): converting {0}", entName);
                if (RendererOgre.GetWorldRenderConv(ent) == null) {
                    // the rendering context is not set up. Odd but not fatal
                    // try again later
                    return false;
                }
                else {
                    // Create the material resource and then make the rendering redisplay
                    RendererOgre.GetWorldRenderConv(ent).CreateMaterialResource(qInstance.priority, m_sceneMgr, ent, m_matName);
                    Ogr.RefreshResourceBF(this.CalculateInterestOrder(ent), Ogr.ResourceTypeMaterial, m_matName);
                }
            }
            else {
                // we couldn't find the entity for the material. not good
                m_log.Log(LogLevel.DBADERROR, "ProcessWaitingMaterials: could not find entity for material {0}", m_matName);
            }
        }
        catch (Exception e) {
            m_log.Log(LogLevel.DBADERROR, "ProcessWaitingMaterials: exception realizing material: {0}", e.ToString());
        }
        return true;
    }

    // ==========================================================================
    /// <summary>
    /// Textures work by the renderer finding it doesn't have the texture. It 
    /// uses a default  texture but then tells us to get the real one. We request
    /// it from the asset servers who put a texture file into the Ogre cache.
    /// Once the texture is there, we tell Ogre to update the texture which
    /// will cause it to appear on the screen.
    /// </summary>
    /// <param name="contextEntity"></param>
    /// <param name="txtName"></param>
    private void RequestTexture(string contextEntity, string txtName) {
        m_log.Log(LogLevel.DRENDERDETAIL, "Request for texture " + txtName);
        EntityNameOgre entName = EntityNameOgre.ConvertOgreResourceToEntityName(txtName);
        // get this work off the thread from the renderer
        // m_workQueue.DoLater(new RequestTextureLater(this, entName));
        m_workQueue.DoLater(RequestTextureLater, entName);
    }

    private bool RequestTextureLater(DoLaterBase qInstance, Object parm) {
        EntityNameOgre m_entName = (EntityNameOgre)parm;
        // note the super kludge since we don't know the real asset context
        // This information is hopefully coded into the entity name
        // The callback can (and will) be called multiple times as the texture gets better resolution
        AssetContextBase.RequestTextureLoad(m_entName, AssetContextBase.AssetType.Texture, TextureLoadedCallback);
        return true;
    }

    // the texture is loaded so get to the right time to tell the renderer
    private void TextureLoadedCallback(string textureEntityName, bool hasTransparancy) {
        LogManager.Log.Log(LogLevel.DRENDERDETAIL, "TextureLoadedCallback: Load complete. Name: {0}", textureEntityName);
        EntityNameOgre entName = new EntityNameOgre(textureEntityName);
        Object[] textureCompleteParameters = { entName, hasTransparancy };
        string ogreResourceName = entName.OgreResourceName;
        if (hasTransparancy) {
            Ogr.RefreshResourceBF(100, Ogr.ResourceTypeTransparentTexture, ogreResourceName);
        }
        else {
            Ogr.RefreshResourceBF(100, Ogr.ResourceTypeTexture, ogreResourceName);
        }
        return;
    }

    // ==========================================================================
    /// <summary>
    /// If there is work queued to happen between frames. Do some of the work now.
    /// </summary>
    private bool ProcessBetweenFrames() {
        return ProcessBetweenFrames(m_betweenFrameTotalCost);
    }

    private bool ProcessBetweenFrames(int cost) {
        // m_log.Log(LogLevel.DRENDERDETAIL, "Process between frames");
        if (m_betweenFramesQueue != null) {
            m_betweenFramesQueue.ProcessQueue(cost);
        }
        return LGB.KeepRunning;
    }

    private bool IsProcessBetweenFramesWork() {
        return ( m_betweenFramesQueue.CurrentQueued > 0 );
    }
}
}
