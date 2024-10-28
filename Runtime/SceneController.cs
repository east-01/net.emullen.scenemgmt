using System;
using FishNet;
using UnityEngine;

namespace EMullen.SceneMgmt 
{
    public partial class SceneController : MonoBehaviour 
    {

        public static SceneController Instance { get; private set; }

        private void Awake() 
        {
            if(Instance != null)
                throw new InvalidOperationException("Tried to create a new SceneDelegate when one already exists.");

            Instance = this;
            DontDestroyOnLoad(this);
        }

        private void OnEnable() 
        {
            UnityEngine.SceneManagement.SceneManager.sceneLoaded += UnitySceneManager_SceneLoaded;
            UnityEngine.SceneManagement.SceneManager.sceneUnloaded += UnitySceneManager_SceneUnloaded;

            InstanceFinder.ClientManager.RegisterBroadcast<SceneSetBroadcast>(RecieveClientSceneSetBroadcast);
            InstanceFinder.ServerManager.RegisterBroadcast<SceneSetBroadcast>(RecieveServerSceneSetBroadcast);
        }

        private void OnDisable() 
        {
            UnityEngine.SceneManagement.SceneManager.sceneLoaded -= UnitySceneManager_SceneLoaded;
            UnityEngine.SceneManagement.SceneManager.sceneUnloaded -= UnitySceneManager_SceneUnloaded;

            InstanceFinder.ClientManager.UnregisterBroadcast<SceneSetBroadcast>(RecieveClientSceneSetBroadcast);
            InstanceFinder.ServerManager.UnregisterBroadcast<SceneSetBroadcast>(RecieveServerSceneSetBroadcast);
        }

    }
}