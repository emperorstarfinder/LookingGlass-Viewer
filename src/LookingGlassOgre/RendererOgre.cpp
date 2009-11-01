/* Copyright (c) Robert Adams
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
#include "StdAfx.h"
#include <direct.h>
#include <stdlib.h>
#include <stdio.h>
#include <errno.h>
#include <sys/stat.h>
#include "RendererOgre.h"
#include "LookingGlassOgre.h"
#include "OLArchive.h"
#include "OLPreloadArchive.h"
#include "ResourceListeners.h"
#include "SkyBoxSimple.h"
#include "SkyBoxSkyX.h"
#include "VisCalcNull.h"
#include "VisCalcFrustDist.h"

namespace RendererOgre {

	RendererOgre::RendererOgre() {
		m_meshSerializer = NULL;
	}

	RendererOgre::~RendererOgre() {
		// TODO: there is a lot of rendering to turn off.
		if (m_sky != NULL) {
			m_sky->Stop();
			m_sky = NULL;
		}
		if (m_visCalc != NULL) {
			m_visCalc->Stop();
			m_visCalc = NULL;
		}
	}

	// The main program calls in here with the main window thread. This is required
	// to keep the windows message pump going and so OpenGL is happy with 
	// its creation happening on the same thread as rendering.
	// The frame rate is capped and sleeps are inserted to return control to
	// the windowing system when the max frame rate is reached.
	// If we don't want the thread, return false.
	Ogre::Timer* timeKeeper = new Ogre::Timer();
	bool RendererOgre::renderingThread() {
		Log("RendererOgre::renderingThread: LookingGlassOrge: Starting rendering");
		// m_root->startRendering();

		// new way that tried to control the amount of work between frames to keep
		//   frame rate up
		int maxFPS = LookingGlassOgr::GetParameterInt("Renderer.Ogre.FramePerSecMax");
		if (maxFPS < 2 || maxFPS > 100) maxFPS = 20;
		int msPerFrame = 1000 / maxFPS;

		unsigned long now = timeKeeper->getMilliseconds();
		unsigned long timeStartedLastFrame = timeKeeper->getMilliseconds();

		while (m_root->renderOneFrame()) {
			Ogre::WindowEventUtilities::messagePump();
			now = timeKeeper->getMilliseconds();
			int remaining = msPerFrame - ((int)(now - timeStartedLastFrame));
			while (remaining > 10) {
				if (m_processBetweenFrame->HasWorkItems()) {
					m_processBetweenFrame->ProcessWorkItems(3);
				}
				else {
					Sleep(remaining);
				}
				now = timeKeeper->getMilliseconds();
				remaining = msPerFrame - ((int)(now - timeStartedLastFrame));
			}
			int totalMSForLastFrame = (int)(timeKeeper->getMilliseconds() - timeStartedLastFrame);
			if (totalMSForLastFrame < 0) totalMSForLastFrame = 1;
			LookingGlassOgr::SetStat(LookingGlassOgr::StatFramesPerSecond, 1000000/totalMSForLastFrame);
			timeStartedLastFrame = timeKeeper->getMilliseconds();
		}
		Log("RendererOgre::renderingThread: Completed rendering");
		destroyScene();
		// for some reason, often after the exit, m_root is not usable
		// m_root->shutdown();
		m_root = NULL;
		return true;
	}

	// As an alternate to using the above rendering thread entry, the main
	// program can call this to render each frame.
	// Note that his also called the message pump to make screen resizing and
	// movement happen on the Ogre frame.
	// If the passed parameter is 'true' we call the windows message pump
	// and the number of ms this frame should take. We do between frame work
	// with any extra time.
	unsigned long m_lastFrameTime;
	bool RendererOgre::renderOneFrame(bool pump, int len) {
		bool ret = false;
		unsigned long now = timeKeeper->getMilliseconds();
		unsigned long timeStartedLastFrame = timeKeeper->getMilliseconds();
		if (m_root != NULL) {
			ret = m_root->renderOneFrame();
			if (pump) Ogre::WindowEventUtilities::messagePump();
		}

		// The amount of time a frame takes to render is passed to us
		// If we have time left over and there is between frame processing, do them
		int remaining = len - ((int)(now - timeStartedLastFrame));
		while (remaining > 10) {
			if (m_processBetweenFrame->HasWorkItems()) {
				m_processBetweenFrame->ProcessWorkItems(20);
			}
			else {
				break;
			}
			now = timeKeeper->getMilliseconds();
			remaining = len - ((int)(now - timeStartedLastFrame));
		}
		int totalMSForLastFrame = (int)(timeKeeper->getMilliseconds() - m_lastFrameTime);
		if (totalMSForLastFrame <= 0) totalMSForLastFrame = 1;
		LookingGlassOgr::SetStat(LookingGlassOgr::StatLastFrameMs, totalMSForLastFrame);
		LookingGlassOgr::SetStat(LookingGlassOgr::StatFramesPerSecond, 1000000/totalMSForLastFrame);
		m_lastFrameTime = timeKeeper->getMilliseconds();

		return ret;
	}

	// Update the camera position given an location and a direction
	void RendererOgre::updateCamera(float px, float py, float pz, 
				float dw, float dx, float dy, float dz,
				float nearClip, float farClip, float aspect) {
		if (m_camera) {
			LookingGlassOgr::Log("RendererOgre::UpdateCamera: pos=<%f, %f, %f>", (double)px, (double)py, (double)pz);
			m_camera->setPosition(px, py, pz);
			m_camera->setOrientation(Ogre::Quaternion(dw, dx, dy, dz));
			if (nearClip != m_camera->getNearClipDistance()) {
				m_camera->setNearClipDistance(nearClip);
			}
			/*	don't fool with far for the moment
			if (farClip != m_camera->getFarClipDistance()) {
				m_camera->setFarClipDistance(farClip);
			}
			*/
			m_visCalc->RecalculateVisibility();
		}
		return;
	}

	// Called from managed code via InitializeOgre().
	// Do all the setup needed in the Ogre environment: all the basic entities
	// (camera, lights, ...), all the resource managers and the user input system.
	void RendererOgre::initialize() {
		Log("RendererOgre::initialize: ");
		m_cacheDir = LookingGlassOgr::GetParameter("Renderer.Ogre.CacheDir");
		m_preloadedDir = LookingGlassOgr::GetParameter("Renderer.Ogre.PreLoadedDir");

		m_defaultTerrainMaterial = LookingGlassOgr::GetParameter("Renderer.Ogre.DefaultTerrainMaterial");
		m_serializeMeshes = LookingGlassOgr::GetParameterBool("Renderer.Ogre.SerializeMeshes");

		m_root = new Ogre::Root(GetParameter("Renderer.Ogre.PluginFilename"));
		Log("RendererOgre::initialize: after new Ogre::Root()");
		// if detail logging is turned off, I don't want Ogre yakking up a storm either
		if (LookingGlassOgr::debugLogCallback == NULL) {
			Ogre::LogManager::getSingleton().setLogDetail(Ogre::LL_LOW);
		}

		try {
			// load the resource info from the Ogre config files
			loadOgreResources(GetParameter("Renderer.Ogre.ResourcesFilename"));
			// set up the render system (window, size, OS connection, ...)
	        configureOgreRenderSystem();

			// setup our special resource groups for meshes and materials
			createLookingGlassResourceGroups();
			// turn on the resource system
	        initOgreResources();
		}
		catch (char* str) {
			Log("RendererOgre::initialize: LookingGlassOrge: exception initializing: {0}", str);
			return;
		}

		// create the viewer components
        createScene();
        createCamera();
        createViewport();
        createSky();
        createVisibilityProcessor();
        createFrameListener();
		if (LookingGlassOgr::userIOCallback != NULL) {
	        createInput();
		}

		// force a first time visibility calculation
		m_visCalc->RecalculateVisibility();

		// uncomment this to generate the loading mesh shape (small cube)
		// GenerateLoadingMesh();
		return;
	}

	void RendererOgre::destroyScene() {
		// TODO: write something here
		return;
	}

	// Load all the resource locations from the resource configuration file
	void RendererOgre::loadOgreResources(const char* resourceFile) {
		Log("RendererOgre::loadOgreResources: ");
		Ogre::String secName, typeName, archName;
		Ogre::ConfigFile cf;
		cf.load(resourceFile);
		Ogre::ConfigFile::SectionIterator seci = cf.getSectionIterator();
		while (seci.hasMoreElements()) {
			secName = seci.peekNextKey();
			Ogre::ConfigFile::SettingsMultiMap *settings = seci.getNext();
			Ogre::ConfigFile::SettingsMultiMap::iterator i;
			for (i = settings->begin(); i != settings->end(); ++i) {
				typeName = i->first;
				archName = i->second;
				Ogre::ResourceGroupManager::getSingleton().addResourceLocation(archName, typeName, secName);
			}
		}
	}

	// Create the resource group and group managers for the LookingGlass Ogre extensions
	void RendererOgre::createLookingGlassResourceGroups() {
		Log("RendererOgre::createLookingGlassResourceGroups:");
		Ogre::ResourceGroupManager::getSingleton().createResourceGroup(OLResourceGroupName);

		// routines for managing and loading materials
		m_materialTracker = new OLMaterialTracker::OLMaterialTracker(this);
		int betweenWork = LookingGlassOgr::GetParameterInt("Renderer.Ogre.BetweenFrame.WorkItems");
		if (betweenWork == 0) betweenWork = 5000;
		m_processBetweenFrame = new ProcessBetweenFrame::ProcessBetweenFrame(this, betweenWork);

		// listener to catch references to materials in meshes when they are read in
		Ogre::MeshManager::getSingleton().setListener(new OLMeshSerializerListener(this));
		// Ogre::ScriptCompilerManager::getSingleton().setListener(new OLScriptCompilerListener(this));

		// Create the archive system that will find the predefined meshes/textures
		Ogre::ArchiveManager::getSingleton().addArchiveFactory(new OLPreloadArchiveFactory() );
		Log("RendererOgre::createLookingGlassResourceGroups: addResourceLocation %s", m_preloadedDir.c_str());
		Ogre::ResourceGroupManager::getSingleton().addResourceLocation(m_preloadedDir,
						OLPreloadTypeName, OLResourceGroupName, true);

		// Create the archive system that will find our meshes
		Ogre::ArchiveManager::getSingleton().addArchiveFactory(new OLArchiveFactory() );
		Log("RendererOgre::createLookingGlassResourceGroups: addResourceLocation %s", m_cacheDir.c_str());
		Ogre::ResourceGroupManager::getSingleton().addResourceLocation(m_cacheDir,
						OLArchiveTypeName, OLResourceGroupName, true);
		return;
	}

	void RendererOgre::configureOgreRenderSystem() {
		Log("RendererOgre::configureOgreRenderSystem:");
		Ogre::String rsystem = GetParameter("Renderer.Ogre.Renderer");
		Ogre::RenderSystem* rs = m_root->getRenderSystemByName(rsystem);
		if (rs == NULL) {
			Log("RendererOgre::configureOgreRenderingSystem: CANNOT INITIALIZE RENDERING SYSTEM '%s'", rsystem);
			return;
		}
		m_root->setRenderSystem(rs);
        rs->setConfigOption("Full Screen", "No");
        rs->setConfigOption("Video Mode", GetParameter("Renderer.Ogre.VideoMode"));

		// Two types of initialization here. Get own window or use a passed window
		Ogre::String windowHandle = LookingGlassOgr::GetParameter("Renderer.Ogre.ExternalWindow.Handle");
		if (windowHandle.length() == 0) {
			m_window = m_root->initialise(true, GetParameter("Renderer.Ogre.Name"));
		}
		else {
			m_window = m_root->initialise(false);
			Ogre::NameValuePairList createParams;
			createParams["externalWindowHandle"] = windowHandle;
			createParams["title"] = GetParameter("Renderer.Ogre.Name");
			// createParams["left"] = something;
			// createParams["right"] = something;
			// createParams["depthBuffer"] = something;
			// createParams["parentWindowHandle"] = something;
			m_window = m_root->createRenderWindow("MAINWINDOW", 
				LookingGlassOgr::GetParameterInt("Renderer.Ogre.ExternalWindow.Width"),
				LookingGlassOgr::GetParameterInt("Renderer.Ogre.ExternalWindow.Height"),
				false, &createParams);
		}
	}

	void RendererOgre::initOgreResources() {
		Log("RendererOgre::initOgreResources");
		Ogre::TextureManager::getSingleton().setDefaultNumMipmaps(
			LookingGlassOgr::GetParameterInt("Renderer.Ogre.DefaultNumMipmaps"));
		Ogre::ResourceGroupManager::getSingleton().initialiseAllResourceGroups();
	}

	void RendererOgre::createScene() {
		Log("RendererOgre::createScene");
		try {
			const char* sceneName = GetParameter("Renderer.Ogre.Name");
			m_sceneMgr = m_root->createSceneManager(Ogre::ST_EXTERIOR_CLOSE, sceneName);
			// m_sceneMgr = m_root->createSceneManager(Ogre::ST_GENERIC, sceneName);
			// ambient has to be adjusted for time of day. Set it initially
			// m_sceneMgr->setAmbientLight(Ogre::ColourValue(0.5, 0.5, 0.5));
			m_sceneMgr->setAmbientLight(LookingGlassOgr::GetParameterColor("Renderer.Ogre.Ambient"));
			const char* shadowName = LookingGlassOgr::GetParameter("Renderer.Ogre.ShadowTechnique");
			if (stricmp(shadowName, "texture-modulative") == 0) {
				m_sceneMgr->setShadowTechnique(Ogre::SHADOWTYPE_TEXTURE_MODULATIVE);	// hardest
				LookingGlassOgr::Log("RendererOgre::createScene: setting shadow to 'texture-modulative'");
			}
			if (stricmp(shadowName, "stencil-modulative") == 0) {
				m_sceneMgr->setShadowTechnique(Ogre::SHADOWTYPE_STENCIL_MODULATIVE);
				LookingGlassOgr::Log("RendererOgre::createScene: setting shadow to 'stencil-modulative'");
			}
			if (stricmp(shadowName, "texture-additive") == 0) {
				m_sceneMgr->setShadowTechnique(Ogre::SHADOWTYPE_TEXTURE_ADDITIVE);	// easiest
				LookingGlassOgr::Log("RendererOgre::createScene: setting shadow to 'texture-additive'");
			}
			if (stricmp(shadowName, "stencil-additive") == 0) {
				m_sceneMgr->setShadowTechnique(Ogre::SHADOWTYPE_STENCIL_ADDITIVE);
				LookingGlassOgr::Log("RendererOgre::createScene: setting shadow to 'stencil-additive'");
			}
			int shadowFarDistance = LookingGlassOgr::GetParameterInt("Renderer.Ogre.ShadowFarDistance");
			m_sceneMgr->setShadowFarDistance((float)shadowFarDistance);
			m_sceneMgr->setShadowColour(Ogre::ColourValue(0.2, 0.2, 0.2));
		}
		catch (std::exception e) {
			Log("RendererOgre::createScene: Exception %s", e.what());
			return;
		}
	}

	void RendererOgre::createCamera() {
		Log("RendererOgre::createCamera");
		m_camera = m_sceneMgr->createCamera("MainCamera");
		m_camera->setPosition(0.0, 0.0, 0.0);
		m_camera->setDirection(0.0, 0.0, -1.0);
		m_camera->setNearClipDistance(2.0);
		m_camera->setFarClipDistance(10000.0);
		// m_camera->setFarClipDistance(0.0);
		m_camera->setAutoAspectRatio(true);
		AssertNonNull(m_camera, "createCamera: m_camera is NULL");
	}

	void RendererOgre::createViewport() {
		Log("RendererOgre::createViewport");
		m_viewport = m_window->addViewport(m_camera);
		m_viewport->setBackgroundColour(Ogre::ColourValue(0.0f, 0.0f, 0.25f));
		m_camera->setAspectRatio((float)m_viewport->getActualWidth() / (float)m_viewport->getActualHeight());
	}

	void RendererOgre::createSky() {
		Log("RendererOgre::createsky");
		const char* skyName = LookingGlassOgr::GetParameter("Renderer.Ogre.Sky");
		if (stricmp(skyName, "SkyX") == 0) {
			LookingGlassOgr::Log("RendererOgre::createSky: using SkyBoxSkyX");
			m_sky = new LGSky::SkyBoxSkyX(this);
		}
		else {
			LookingGlassOgr::Log("RendererOgre::createSky: using SkyBoxSimple");
			m_sky = new LGSky::SkyBoxSimple(this);
		}
		m_sky->Initialize();
		m_sky->Start();
	}

	void RendererOgre::createVisibilityProcessor() {
		Log("RendererOgre::createVisibilityProcessor");
		const char* visName = LookingGlassOgr::GetParameter("Renderer.Ogre.Visibility.Processor");
		if (stricmp(visName, "FrustrumDistance") == 0) {
			LookingGlassOgr::Log("RendererOgre::createVisibilityProcessor: using VisCalcFrustDist");
			m_visCalc = new VisCalc::VisCalcFrustDist(this);
		}
		else {
			LookingGlassOgr::Log("RendererOgre::creteVisibilityProcessor: using VisCalcNull");
			m_visCalc = new VisCalc::VisCalcNull(this);
		}
		m_visCalc->Initialize();
		m_visCalc->Start();
	}

	void RendererOgre::createFrameListener() {
		Log("RendererOgre::createFrameListener");
		// this creates two pointers to our base object. 
		// Might need to manage if we ever get dynamic.
		m_root->addFrameListener(this);
	}

	void RendererOgre::createInput() {
		m_userio = new UserIO(this);
	}

	// ========== Ogre::FrameListener
	bool RendererOgre::frameStarted(const Ogre::FrameEvent& evt) {
		return true;
	}

	bool RendererOgre::frameRenderingQueued(const Ogre::FrameEvent& evt) {
		return true;
	}

	int betweenFrameCounter = 0;
	bool RendererOgre::frameEnded(const Ogre::FrameEvent& evt) {
		if (m_window->isClosed()) return false;	// if you close the window we leave
		LookingGlassOgr::IncStat(LookingGlassOgr::StatTotalFrames);
		betweenFrameCounter++;
		if (LookingGlassOgr::betweenFramesCallback != NULL) {
			// the C# code uses this for terrain and regions so don't do it often
			if ((betweenFrameCounter % 10) == 0) {
				return (*LookingGlassOgr::betweenFramesCallback)();
			}
		}
		return true;
	}

	// ========== end of Ogre::FrameListener

	// ============= REQUESTS TO DO WORK
	// BETWEEN FRAME OPERATION
	void RendererOgre::AddEntity(Ogre::SceneManager* sceneMgr, Ogre::SceneNode* sceneNode,
							const char* entName, const char* meshName) {
		// Log("RendererOgre::AddEntity: declare %s, t=%s, g=%s", meshName,
		// 						"Mesh", meshResourceGroupName);
		Ogre::ResourceGroupManager::getSingleton().declareResource(meshName,
								"Mesh", OLResourceGroupName
								);
		Ogre::MovableObject* ent = sceneMgr->createEntity(entName, meshName);
		// it's not scenery
		ent->removeQueryFlags(Ogre::SceneManager::WORLD_GEOMETRY_TYPE_MASK);	
		// Ogre::MovableObject* ent = sceneMgr->createEntity(entName, Ogre::SceneManager::PT_SPHERE);	// DEBUG
		// this should somehow be settable
		ent->setCastShadows(true);
		sceneNode->attachObject(ent);
		m_visCalc->RecalculateVisibility();
		return;
	}

	// BETWEEN FRAME OPERATION
	Ogre::SceneNode* RendererOgre::CreateSceneNode( Ogre::SceneManager* sceneMgr, const char* nodeName,
					Ogre::SceneNode* parentNode,
					bool inheritScale, bool inheritOrientation,
					float px, float py, float pz,
					float sx, float sy, float sz,
					float ow, float ox, float oy, float oz) {
		Ogre::SceneNode* node = NULL;
		Ogre::Quaternion rot = Ogre::Quaternion(ow, ox, oy, oz);

		if (parentNode == 0) {
			node = sceneMgr->getRootSceneNode()->createChildSceneNode(nodeName);
		}
		else {
			node = parentNode->createChildSceneNode(nodeName);
		}
		node->setInheritScale(inheritScale);
		node->setInheritOrientation(inheritOrientation);
		node->setScale(sx, sy, sz);
		node->translate(px, py, pz);
		node->rotate(rot);
		node->setVisible(true);
		node->setInitialState();
		return node;
	}

	// BETWEEN FRAME OPERATION
	void RendererOgre::UpdateSceneNode(const char* entName,
					bool updatePosition, float px, float py, float pz, 
					bool updateScale, float sx, float sy, float sz,
					bool updateRotation, float ow, float ox, float oy, float oz) {
		if (m_sceneMgr->hasSceneNode(entName)) {
			Ogre::SceneNode* sceneNode = m_sceneMgr->getSceneNode(entName);
			if (updatePosition) {
				sceneNode->setPosition(px, py, pz);
			}
			if (updateScale) {
				sceneNode->setScale(sx, sy, sz);
			}
			if (updateRotation) {
				sceneNode->setOrientation(ow, ox, oy, oz);
			}
		}
		else {
			LookingGlassOgr::Log("RendererOgre::UpdateSceneNode: entity not found. Did not update entity %s", entName);
		}
		return;
	}

	// Passed a bunch of vertices and index information, create the mesh that goes with it.
	// The mesh is created and serialized to a .mesh file which just happens to be in the 
	// same spot as the resource looker-upper will look to find it when the mesh is reloaded.
	// BETWEEN FRAME OPERATION
	void RendererOgre::CreateMeshResource(const char* eName, const int faceCounts[], const float faceVertices[]) {
		Ogre::String entName = eName;
		Ogre::String manualObjectName = "MO/" + entName;
		Ogre::String baseMaterialName = entName;
		const int* fC = &faceCounts[1];
		const float* fV = &faceVertices[1];
		const int faces = *fC;
		fC += 1;

		Ogre::ManualObject* mo = m_sceneMgr->createManualObject(manualObjectName);
		mo->setCastShadows(true);
		// Ogre::ManualObject* mo = new Ogre::ManualObject(manualObjectName);
		Log("RendererOgre::CreateMeshResource: Creating mo. f = %d, %s", faces, manualObjectName.c_str());

		int iface, iv;
		const float* fVf;
		char faceName[10];
		Ogre::String materialName;
		for (iface = 0; iface < faces; iface++) {
			itoa(iface, faceName, 10);
			materialName = baseMaterialName + "-" + faceName + ".material";
			mo->begin(materialName);
			fVf = fV + fC[0];
			// Log("RendererOgre::CreateMeshResource: F%d: vertices %d, %d, %d", iface, fC[0], fC[1], fC[2]);
			for (iv=0; iv < fC[1]; iv++) {
				// Log("RendererOgre::CreateMeshResource: %f, %f, %f, %f, %f", fVf[0], fVf[1], fVf[2], fVf[3], fVf[4] );
				mo->position(fVf[0], fVf[1], fVf[2]);
				mo->textureCoord(fVf[3], fVf[4]);
				mo->normal(fVf[5], fVf[6], fVf[7]);
				fVf += fC[2];
			}
			fC += 3;
			fVf = fV + fC[0];
			// Log("RendererOgre::CreateMeshResource: F%d: indices %d, %d, %d", iface, fC[0], fC[1], fC[2]);
			for (iv=0; iv < fC[1]; iv += 3) {
				// Log("RendererOgre::CreateMeshResource: %f, %f, %f", fVf[0], fVf[1], fVf[2]);
				mo->triangle((Ogre::uint32)fVf[0], (Ogre::uint32)fVf[1], (Ogre::uint32)fVf[2]);
				// mo->index((Ogre::uint32)fVf[0]);
				// mo->index((Ogre::uint32)fVf[1]);
				// mo->index((Ogre::uint32)fVf[2]);
				fVf += fC[2];
			}
			fC += 3;
			mo->end();
		}

		Log("RendererOgre::CreateMeshResource: converting to mesh: %s", entName.c_str());
		// I thought I should have to find and unload the old mesh but
		// these do not the right things and don't know why. Removing comments causes crashes.
		// if (Ogre::MeshManager::getSingleton().resourceExists(entName)) {
		// 	Ogre::MeshManager::getSingleton().unload(entName);
		// 	Ogre::MeshManager::getSingleton().remove(entName);
		// }
		try {
			// Ogre::MeshManager::getSingleton().load(entName);
			Ogre::MeshPtr mesh = mo->convertToMesh(entName , OLResourceGroupName);
			mo->clear();
			m_sceneMgr->destroyManualObject(mo);
			mo = 0;

			mesh->buildEdgeList();
			// mesh->generateLodLevels(m_lodDistances, Ogre::ProgressiveMesh::VertexReductionQuota::VRQ_PROPORTIONAL, 0.5f);

			if (m_serializeMeshes) {
				// serialize the mesh to the filesystem
				meshToResource(mesh, entName);
		}
		}
		catch (Ogre::Exception &e) {
			Log("RendererOgre::CreateMeshResource: failure generating mesh: %s", e.getDescription().c_str());
			// This will leave the mesh as the default loading shape
			// and potentially create an ManualObject leak
		}


		return;
	}

	// Create a simple cube to be the loading mesh representation
	void RendererOgre::GenerateLoadingMesh() {
		Ogre::String loadingMeshName = "LookingGlass/LoadingShape";
		Ogre::String loadingMaterialName = "LookingGlass/LoadingShape";
		Ogre::String targetFilename = LookingGlassOgr::GetParameter("Renderer.Ogre.DefaultMeshFilename");

		Ogre::String loadingMeshManualObjectName = "MO/" + loadingMeshName;

		Ogre::ManualObject* mo = m_sceneMgr->createManualObject(loadingMeshManualObjectName);
		Log("RendererOgre::GenerateLoadingMesh: ");

		mo->begin(loadingMaterialName);
		/*
		mo->position(0.0, 0.0, 0.0);
		mo->position(0.0, 0.0, 1.0);
		mo->position(0.0, 1.0, 0.0);
		mo->position(0.0, 1.0, 1.0);
		mo->position(1.0, 0.0, 0.0);
		mo->position(1.0, 0.0, 1.0);
		mo->position(1.0, 1.0, 0.0);
		mo->position(1.0, 1.0, 1.0);
		
		mo->quad(0, 2, 3, 1);
		mo->quad(2, 6, 7, 3);
		mo->quad(0, 4, 6, 2);
		mo->quad(0, 4, 5, 1);
		mo->quad(4, 5, 7, 6);
		mo->quad(7, 5, 1, 3);
		*/
		// top
		mo->position(0.0, 1.0, 0.0);
		mo->position(1.0, 1.0, 0.0);
		mo->position(1.0, 0.0, 0.0);
		mo->position(0.0, 0.0, 0.0);
		mo->triangle(0, 1, 2);
		mo->triangle(0, 2, 3);
		
		// bottom
		mo->position(1.0, 1.0, 1.0);
		mo->position(0.0, 1.0, 1.0);
		mo->position(0.0, 0.0, 1.0);
		mo->position(1.0, 0.0, 1.0);
		mo->triangle(4, 5, 6);
		mo->triangle(4, 6, 7);

		// sides
		mo->triangle(5, 0, 3);
		mo->triangle(5, 3, 6);

		mo->triangle(1, 0, 5);
		mo->triangle(1, 5, 4);

		mo->triangle(7, 1, 4);
		mo->triangle(7, 2, 1);

		mo->triangle(3, 2, 7);
		mo->triangle(3, 7, 6);

		mo->end();

		Ogre::MeshPtr mesh = mo->convertToMesh(loadingMeshName , OLResourceGroupName);
		mo->clear();
		m_sceneMgr->destroyManualObject(mo);

		if (m_meshSerializer == NULL) {
			m_meshSerializer = new Ogre::MeshSerializer();
		}
		CreateParentDirectory(targetFilename);
		m_meshSerializer->exportMesh(mesh.getPointer(), targetFilename);

		// since this is called only once, we don't bother freeing the mesh
		return;
	}

	// Passed a bunch of vertices and index information, create the mesh that goes with it.
	// The mesh is created and serialized to a .mesh file which just happens to be in the 
	// same spot as the resource looker-upper will look to find it when the mesh is reloaded.
	// NOTE: IN PROGRESS: an attempt to build the mesh directly rather than using ManualObject.
	// BETWEEN FRAME OPERATION
	void RendererOgre::CreateMeshResource2(const char* eName, const int faceCounts[], const float faceVertices[]) {
		Ogre::String entName = eName;
		Ogre::String manualObjectName = "MO/" + entName;
		Ogre::String baseMaterialName = entName;
		const int* fC = &faceCounts[1];
		const float* fV = &faceVertices[1];
		const int faces = *fC;
		fC += 1;

		Ogre::MeshPtr manualMesh = Ogre::MeshManager::getSingleton().createManual(manualObjectName, OLResourceGroupName);
		LookingGlassOgr::Log("RendererOgre::CreateMeshResource2: Creating mo. f = %d, %s", 
				faces, manualMesh->getName().c_str());

			/*
		int iface, iv;
		const float* fVf;
		char faceName[10];
		Ogre::String materialName;
		for (iface = 0; iface < faces; iface++) {
			itoa(iface, faceName, 10);
			materialName = baseMaterialName + "-" + faceName + ".material";
			Ogre::SubMesh* faceMesh = manualMesh->createSubMesh();
			faceMesh->setMaterialName(materialName);
			Ogre::VertexData faceVerts = new Ogre::VertexData(fC[2]);

			faceMesh->vertexData = newVertexData;

			fVf = fV + fC[0];
			// Log("RendererOgre::CreateMeshResource2: F%d: vertices %d, %d, %d", iface, fC[0], fC[1], fC[2]);
			for (iv=0; iv < fC[1]; iv++) {
				// Log("RendererOgre::CreateMeshResource2: %f, %f, %f, %f, %f", fVf[0], fVf[1], fVf[2], fVf[3], fVf[4] );
				mo->position(fVf[0], fVf[1], fVf[2]);
				mo->textureCoord(fVf[3], fVf[4]);
				fVf += fC[2];
			}
			fC += 3;
			fVf = fV + fC[0];
			// Log("RendererOgre::CreateMeshResource2: F%d: indices %d, %d, %d", iface, fC[0], fC[1], fC[2]);
			for (iv=0; iv < fC[1]; iv += 3) {
				// Log("RendererOgre::CreateMeshResource2: %f, %f, %f", fVf[0], fVf[1], fVf[2]);
				mo->index((Ogre::uint32)fVf[0]);
				mo->index((Ogre::uint32)fVf[1]);
				mo->index((Ogre::uint32)fVf[2]);
				fVf += fC[2];
			}
			fC += 3;
			mo->end();
		}
			*/

		if (m_serializeMeshes) {
			// serialize the mesh to the filesystem
			meshToResource(manualMesh, entName);
		}

		return;
	}

	// BETWEEN FRAME OPERATION
void RendererOgre::meshToResource(Ogre::MeshPtr mesh, const Ogre::String entName) {
	Log("OLMeshManager::meshToResource: creating mesh for %s", entName.c_str());
	Ogre::String targetFilename = EntityNameToFilename(entName, "");

	// Make sure the directory exists -- I wish the serializer did this for me
	CreateParentDirectory(targetFilename);
	
	if (m_meshSerializer == NULL) {
		m_meshSerializer = new Ogre::MeshSerializer();
	}
	m_meshSerializer->exportMesh(mesh.getPointer(), targetFilename);

	// with the mesh on the disk, get rid of the one in memory and let it reload
	// is the next step necessary?
	// mesh->unload();
	// Ogre::MeshManager::getSingleton().remove(mesh->getHandle());
}

Ogre::String RendererOgre::EntityNameToFilename(const Ogre::String entName, const Ogre::String suffix) {
	Ogre::String fullFilename = m_cacheDir;
	fullFilename += "/";
	fullFilename += entName;
	fullFilename += suffix;
	return fullFilename;
}
// Given a filename, make sure all it's parent directories exist
void RendererOgre::CreateParentDirectory(const Ogre::String filename) {
	// make any backslashes into forward slashes
	Ogre::String fn = filename;
	Ogre::String::size_type ii;
	while ((ii = fn.find_first_of("\\")) != Ogre::String::npos) {
		fn.replace(ii, 1, 1, '/');
	}
	MakeParentDir(fn);
	return;
}

void RendererOgre::MakeParentDir(const Ogre::String filename) {
	Ogre::String::size_type lastSlash = filename.find_last_of('/');
	Ogre::String dirName = filename.substr(0, lastSlash);
	int iResult = _mkdir(dirName.c_str());			// try to make the directory
	if (iResult != 0) {							// if it couldn't be made
		if (errno == ENOENT) {					// if it couldn't make because no parents
			// Log("RendererOgre::MakeParentDir: recursing for %s", dirName.c_str());
			MakeParentDir(dirName);				// create the parent directory
			_mkdir(dirName.c_str());				// make the directory this time
		}
	}
	return;
}

// Given a scene node for a terrain, find the manual object on that scene node and
// update the manual object with the heightmap passed. If  there is no manual object on
// the scene node, remove all it's attachments and add the manual object.
// The heightmap is passed in a 1D array ordered by width rows (for(width) {for(length) {hm[w,l]}})
// This must be called between frames since it touches the scene graph
// BETWEEN FRAME OPERATION
void RendererOgre::GenTerrainMesh(Ogre::SceneManager* sceneMgr, Ogre::SceneNode* node, 
								  const int hmWidth, const int hmLength, const float* hm) {

	// Find the movable object attached to the scene node. If not found remove all.
	if (node->numAttachedObjects() > 0) {
		Ogre::MovableObject* attached = node->getAttachedObject(0);
		if (attached->getMovableType() != "ManualObject") {
            // don't know why this would ever happen but clean out the odd stuff
            Log("Found extra stuff on terrain scene node");
			node->detachAllObjects();
		}
	}
	// if there is not a manual object on the node, create a new one
	if (node->numAttachedObjects() == 0) {
		Log("GenTerrainMesh: creating terrain ManualObject");
        // if no attached objects, we add our dynamic ManualObject
		Ogre::ManualObject* mob = sceneMgr->createManualObject("ManualObject/" + node->getName());
		mob->addQueryFlags(Ogre::SceneManager::WORLD_GEOMETRY_TYPE_MASK);
		mob->setDynamic(true);
		mob->setCastShadows(true);
		mob->setVisible(true);
		mob->setQueryFlags(Ogre::SceneManager::WORLD_GEOMETRY_TYPE_MASK);
		node->attachObject(mob);
		m_visCalc->RecalculateVisibility();
	}

	Ogre::ManualObject* mo = (Ogre::ManualObject*)node->getAttachedObject(0);

	// stuff our heightmap information into the dynamic manual object
	mo->estimateVertexCount(hmWidth * hmLength);
	mo->estimateIndexCount(hmWidth * hmLength * 6);

	if (mo->getNumSections() == 0) {
		mo->begin(m_defaultTerrainMaterial);	// if first time
	}
	else {
		mo->beginUpdate(0);					// we've been here before
	}

	int loc = 0;
	for (int xx = 0; xx < hmWidth; xx++) {
		for (int yy = 0; yy < hmLength; yy++) {
			mo->position(xx, yy, hm[loc++]);
			mo->textureCoord((float)xx / (float)hmWidth, (float)yy / (float)hmLength);
			mo->normal(0.0, 1.0, 0.0);	// always up (for the moment)
		}
	}

	for (int px = 0; px < hmLength-1; px++) {
		for (int py = 0; py < hmWidth-1; py++) {
			mo->quad(px      + py       * hmWidth,
					 px      + (py + 1) * hmWidth,
					(px + 1) + (py + 1) * hmWidth,
					(px + 1) + py       * hmWidth
					 );
		}
	}

	mo->end();

	return;
}

// BETWEEN FRAME OPERATION
void RendererOgre::AddOceanToRegion(Ogre::SceneManager* sceneMgr, Ogre::SceneNode* regionNode,
									const float width, const float length, const float waterHeight, const char* wName) {
	if (sceneMgr == 0) {
		Log("AddOceanToRegion: passed null scene manager");
		return;
	}
	Ogre::String waterName = wName;
	Ogre::Plane* oceanPlane = new Ogre::Plane(0.0, 0.0, 1.0, 0);
	Ogre::MeshPtr oceanMesh = Ogre::MeshManager::getSingleton().createPlane(waterName, OLResourceGroupName, 
					*oceanPlane, width, length,
					2, 2, true,
					2, 2.0, 2.0, Ogre::Vector3::UNIT_Y);
	Ogre::String oceanMaterialName = LookingGlassOgr::GetParameter("Renderer.Ogre.OceanMaterialName");
	Log("AddOceanToRegion: r=%s, h=%f, n=%s, m=%s", 
		regionNode->getName().c_str(), waterHeight, wName, oceanMaterialName.c_str());
	oceanMesh->getSubMesh(0)->setMaterialName(oceanMaterialName);
	Ogre::Entity* oceanEntity = sceneMgr->createEntity("WaterEntity/" + waterName, oceanMesh->getName());
	oceanEntity->addQueryFlags(Ogre::SceneManager::WORLD_GEOMETRY_TYPE_MASK);
	oceanEntity->setCastShadows(false);
	Ogre::SceneNode* oceanNode = regionNode->createChildSceneNode("WaterSceneNode/" + waterName);
	oceanNode->setInheritOrientation(true);
	oceanNode->setInheritScale(false);
	oceanNode->translate(width/2.0, length/2.0, waterHeight);
	oceanNode->attachObject(oceanEntity);
	m_visCalc->RecalculateVisibility();
	return;
}

// ============= UTILITY ROUTINES
// call out to the main program and make sure we should keep running
const bool RendererOgre::checkKeepRunning() {
	if (LookingGlassOgr::checkKeepRunningCallback != NULL) {
		return (*LookingGlassOgr::checkKeepRunningCallback)();
	}
	return false;
}

// Routine which calls back into the managed world to fetch a string/value configuration
// parameter.
const char* RendererOgre::GetParameter(const char* paramName) {
	if (LookingGlassOgr::fetchParameterCallback != NULL) {
		return (*LookingGlassOgr::fetchParameterCallback)(paramName);
	}
	else {
		Log("RendererOgre::GetParameter: could not get parameter %s", paramName);
	}
	return NULL;
}

// Print out a message of the pointer thing is null. At least the log will know
// of the problem
void RendererOgre::AssertNonNull(void* thing, const char* msg) {
	if (thing == NULL) {
		Log(msg);
	}
}

// Call back into the managed world to output a log message with formatting
void RendererOgre::Log(const char* msg, ...) {
	char buff[1024];
	if (LookingGlassOgr::debugLogCallback != NULL) {
		va_list args;
		va_start(args, msg);
		vsprintf(buff, msg, args);
		va_end(args);
		(*LookingGlassOgr::debugLogCallback)(buff);
	}
}

// Do a printf and return a newly allocated buffer (caller has to free it)
char* RendererOgre::formatIt(const char* msg, ...) {
	char* buff = (char*)OGRE_MALLOC(256, Ogre::MEMCATEGORY_GENERAL);
	va_list args;
	va_start(args, msg);
	vsnprintf(buff, 256, msg, args);
	va_end(args);
	return buff;
}

// Do a printf and return a newly allocated buffer (caller has to free it)
void RendererOgre::formatIt(Ogre::String& dst, const char* msg, ...) {
	char buff[1024];
	va_list args;
	va_start(args, msg);
	vsnprintf(buff, 256, msg, args);
	va_end(args);
	dst = buff;
	return;
}

}

