using System.Collections.Generic;
using FishNet.Broadcast;
using FishNet.Connection;
using FishNet.Managing.Scened;
using UnityEngine.SceneManagement;

namespace EMullen.SceneMgmt 
{
    public struct ClientSceneChangeBroadcast : IBroadcast 
    {
        public List<SceneLookupData> scenes;
        public SceneLookupData latestSceneLoad;
        public LoadSceneMode? loadSceneMode;
        public Cause cause;

        private ClientSceneChangeBroadcast(List<SceneLookupData> scenes, SceneLookupData latestSceneLoad, LoadSceneMode? loadSceneMode, Cause cause) 
        {
            this.scenes = scenes;
            this.latestSceneLoad = latestSceneLoad;
            this.loadSceneMode = loadSceneMode;
            this.cause = cause;
        }

        public static ClientSceneChangeBroadcast LoadBroadcastFactory(List<SceneLookupData> scenes, SceneLookupData loadedScene, LoadSceneMode loadSceneMode) 
        => new(scenes, loadedScene, loadSceneMode, Cause.LOAD);
        public static ClientSceneChangeBroadcast UnloadBroadcastFactory(List<SceneLookupData> scenes, SceneLookupData loadedScene) 
        => new(scenes, loadedScene, null, Cause.UNLOAD);

        public enum Cause { LOAD, UNLOAD }
    }

    public struct ClientNetworkedScene : IBroadcast 
    {
        public SceneLookupData scene;
        public Action action;

        public ClientNetworkedScene(SceneLookupData scene, Action action) {
            this.scene = scene;
            this.action = action;
        }

        public enum Action { ADD, REMOVE }
    }

    public struct SceneSyncBroadcast : IBroadcast 
    {
        public List<SceneLookupData> scenes;
        public SceneLookupData activeScene;
        public SceneSyncBroadcast(List<SceneLookupData> scenes, SceneLookupData activeScene = null) 
        {
            this.scenes = scenes;
            this.activeScene = activeScene;
        }
    }
}