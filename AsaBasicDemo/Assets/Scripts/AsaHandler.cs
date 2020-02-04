using Microsoft.Azure.SpatialAnchors;
using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.XR.WSA;
using UnityEngine.XR.WSA.Input;

public class AsaHandler : MonoBehaviour
{
    public TextMesh LogText;
    public GameObject Prefab;

    private GameObject _prefabInstance;
    private Material _prefabInstanceMaterial;

    private GestureRecognizer _gestureRecognizer;
    private bool _wasTapped = false;

    private CloudSpatialAnchorSession _cloudSpatialAnchorSession;
    private CloudSpatialAnchor _currentCloudAnchor;
    private string _currentCloudAnchorId = String.Empty;
    private float _recommendedSpatialDataForUpload = 0;

    /// <summary>
    /// Our queue of actions that will be executed on the main thread.
    /// </summary>
    private readonly ConcurrentQueue<Action> _dispatchQueue;

    public AsaHandler()
    {
        _dispatchQueue = new ConcurrentQueue<Action>();
    }

    void Start()
    {
        _gestureRecognizer = new GestureRecognizer();
        _gestureRecognizer.StartCapturingGestures();
        _gestureRecognizer.SetRecognizableGestures(GestureSettings.Tap);
        _gestureRecognizer.Tapped += OnTap;

        InitializeSession();
    }

    public void OnTap(TappedEventArgs tapEvent)
    {
        if (_wasTapped)
        {
            return;
        }
        _wasTapped = true;

        // We have saved an anchor, so we will now look for it.
        if (!String.IsNullOrEmpty(_currentCloudAnchorId))
        {
            Debug.Log("ASA Info: We will look for a placed anchor.");
            _wasTapped = true;

            ResetSession(() =>
            {
                InitializeSession();

                // Create a Watcher to look for the anchor we created.
                AnchorLocateCriteria criteria = new AnchorLocateCriteria();
                criteria.Identifiers = new string[] { _currentCloudAnchorId };
                _cloudSpatialAnchorSession.CreateWatcher(criteria);

                Debug.Log("ASA Info: Watcher created. Number of active watchers: " + _cloudSpatialAnchorSession.GetActiveWatchers().Count);
            });
        }
        else
        {
            Log("Creating new anchor...");

            // Clean up any anchors that have been placed.
            CleanupObjects();

            // Construct a Ray using forward direction of the HoloLens.
            Ray GazeRay = new Ray(tapEvent.headPose.position, tapEvent.headPose.forward);

            // Raycast to get the hit point in the real world.
            Physics.Raycast(GazeRay, out RaycastHit hitInfo, float.MaxValue);

            CreateAndSaveAnchor(hitInfo.point);
        }
    }

    /// <summary>
    /// Creates a sphere at the hit point, and then saves a CloudSpatialAnchor there.
    /// </summary>
    /// <param name="hitPoint">The hit point.</param>
    private void CreateAndSaveAnchor(Vector3 hitPoint)
    {
        // Create a white sphere.
        _prefabInstance = GameObject.Instantiate(Prefab, hitPoint, Quaternion.identity) as GameObject;
        var localAnchor = _prefabInstance.AddComponent<WorldAnchor>();
        _prefabInstanceMaterial = _prefabInstance.GetComponent<MeshRenderer>().material;
        _prefabInstanceMaterial.color = Color.white;
        Debug.Log("ASA Info: Created a local anchor.");

        // Create CloudSpatialAnchor and add the local anchor
        _currentCloudAnchor = new CloudSpatialAnchor
        {
            LocalAnchor = localAnchor.GetNativeSpatialAnchorPtr()
        };
        Task.Run(async () =>
        {
            // Wait for enough data about the environment.
            while (_recommendedSpatialDataForUpload < 1.0F)
            {
                await Task.Delay(150);
            }

            try
            {
                QueueOnUpdate(() =>
                {
                    // We are about to save the CloudSpatialAnchor to the Azure Spatial Anchors, turn it yellow.
                    _prefabInstanceMaterial.color = Color.yellow;
                });

                await _cloudSpatialAnchorSession.CreateAnchorAsync(_currentCloudAnchor);

                if (_currentCloudAnchor != null)
                {
                    // Allow the user to tap again to clear state and look for the anchor.
                    _wasTapped = false;

                    // Record the identifier to locate.
                    _currentCloudAnchorId = _currentCloudAnchor.Identifier;

                    QueueOnUpdate(() =>
                    {
                        // Turn the sphere blue.
                        _prefabInstanceMaterial.color = Color.blue;
                        LogText.color = Color.blue;
                        LogText.text = $"Saved anchor to Azure Spatial Anchors: {_currentCloudAnchorId}";
                    });

                    Debug.Log("ASA Info: Saved anchor to Azure Spatial Anchors! Identifier: " + _currentCloudAnchorId);
                }
                else
                {
                    QueueOnUpdate(() =>
                    {
                        _prefabInstanceMaterial.color = Color.red;
                    });
                    Debug.LogError("ASA Error: Failed to save, but no exception was thrown.");
                }
            }
            catch (Exception ex)
            {
                QueueOnUpdate(() =>
                {
                    _prefabInstanceMaterial.color = Color.red;
                });
                Debug.LogError("ASA Error: " + ex.Message);
            }
        });
    }

    /// <summary>
    /// Initializes a new CloudSpatialAnchorSession.
    /// </summary>
    void InitializeSession()
    {
        Log("Initializing CloudSpatialAnchorSession.");

        // Provide the Azure Spatial Anchors AccountId and Primary Key below
        if (String.IsNullOrEmpty(ConstantsSecret.AsaAccountId) || String.IsNullOrEmpty(ConstantsSecret.AsaKey))
        {
            Log("Azure Spatial Anchors Account ID or Account Key are not set.", Color.red);
            return;
        }

        _cloudSpatialAnchorSession = new CloudSpatialAnchorSession();
        _cloudSpatialAnchorSession.Configuration.AccountId = ConstantsSecret.AsaAccountId.Trim();
        _cloudSpatialAnchorSession.Configuration.AccountKey = ConstantsSecret.AsaKey.Trim();
        _cloudSpatialAnchorSession.AnchorLocated += CloudSpatialAnchorSession_AnchorLocated;
        _cloudSpatialAnchorSession.LocateAnchorsCompleted += CloudSpatialAnchorSession_LocateAnchorsCompleted;

        _cloudSpatialAnchorSession.LogLevel = SessionLogLevel.All;

        _cloudSpatialAnchorSession.Error += CloudSpatialAnchorSession_Error;
        _cloudSpatialAnchorSession.OnLogDebug += CloudSpatialAnchorSession_OnLogDebug;
        _cloudSpatialAnchorSession.SessionUpdated += CloudSpatialAnchorSession_SessionUpdated;

        _cloudSpatialAnchorSession.Start();

        Debug.Log("ASA Info: Session was initialized.");
    }

    /// <summary>
    /// Cleans up objects.
    /// </summary>
    public void CleanupObjects()
    {
        if (_prefabInstance != null)
        {
            Destroy(_prefabInstance);
            _prefabInstance = null;
        }

        if (_prefabInstanceMaterial != null)
        {
            Destroy(_prefabInstanceMaterial);
            _prefabInstanceMaterial = null;
        }

        _currentCloudAnchor = null;
    }

    /// <summary>
    /// Cleans up objects and stops the CloudSpatialAnchorSessions.
    /// </summary>
    public void ResetSession(Action completionRoutine = null)
    {
        Debug.Log("ASA Info: Resetting the session.");

        if (_cloudSpatialAnchorSession.GetActiveWatchers().Count > 0)
        {
            Debug.LogError("ASA Error: We are resetting the session with active watchers, which is unexpected.");
        }

        CleanupObjects();

        _cloudSpatialAnchorSession.Reset();

        _dispatchQueue.Enqueue(() =>
        {
            if (_cloudSpatialAnchorSession != null)
            {
                _cloudSpatialAnchorSession.Stop();
                _cloudSpatialAnchorSession.Dispose();
                Debug.Log("ASA Info: Session was reset.");
                completionRoutine?.Invoke();
            }
            else
            {
                Debug.LogError("ASA Error: cloudSpatialAnchorSession was null, which is unexpected.");
            }
        });
    }

    private void CloudSpatialAnchorSession_Error(object sender, SessionErrorEventArgs args)
    {
        Debug.LogError("ASA Error: " + args.ErrorMessage);
    }

    private void CloudSpatialAnchorSession_OnLogDebug(object sender, OnLogDebugEventArgs args)
    {
        Debug.Log("ASA Log: " + args.Message);
    }

    private void CloudSpatialAnchorSession_SessionUpdated(object sender, SessionUpdatedEventArgs args)
    {
        Debug.Log("ASA Log: recommendedForCreate: " + args.Status.RecommendedForCreateProgress);
        _recommendedSpatialDataForUpload = args.Status.RecommendedForCreateProgress;
    }

    private void CloudSpatialAnchorSession_AnchorLocated(object sender, AnchorLocatedEventArgs args)
    {
        switch (args.Status)
        {
            case LocateAnchorStatus.Located:
                Log($"Anchor located: {args.Identifier}", Color.green);
                QueueOnUpdate(() =>
                {
                    // Create a green sphere.
                    _prefabInstance = GameObject.Instantiate(Prefab, Vector3.zero, Quaternion.identity) as GameObject;
                    var localAnchor = _prefabInstance.AddComponent<WorldAnchor>();
                    _prefabInstanceMaterial = _prefabInstance.GetComponent<MeshRenderer>().material;
                    _prefabInstanceMaterial.color = Color.green;

                    // Get the WorldAnchor from the CloudSpatialAnchor and assign it to the local anchor 
                    localAnchor.SetNativeSpatialAnchorPtr(args.Anchor.LocalAnchor);

                    // Clean up state so that we can start over and create a new anchor.
                    _currentCloudAnchorId = String.Empty;
                    _wasTapped = false;
                });
                break;
            case LocateAnchorStatus.AlreadyTracked:
                Debug.Log("ASA Info: Anchor already tracked. Identifier: " + args.Identifier);
                break;
            case LocateAnchorStatus.NotLocated:
                Debug.Log("ASA Info: Anchor not located. Identifier: " + args.Identifier);
                break;
            case LocateAnchorStatus.NotLocatedAnchorDoesNotExist:
                Debug.LogError("ASA Error: Anchor not located does not exist. Identifier: " + args.Identifier);
                break;
        }
    }

    private void CloudSpatialAnchorSession_LocateAnchorsCompleted(object sender, LocateAnchorsCompletedEventArgs args)
    {
        Debug.Log("ASA Info: Locate anchors completed. Watcher identifier: " + args.Watcher.Identifier);
    }

    private void Update()
    {
        if (_dispatchQueue.TryDequeue(out Action action))
        {
            action();
        }
    }

    private void Log(string text, Color? color = null)
    {
        if (LogText != null)
        {
            QueueOnUpdate(() =>
            {
                LogText.text = text;
                LogText.color = color ?? Color.gray;
            });
        }
        else
        {
            Debug.LogError("Log text control is not assigned.");
        }
        Debug.Log(text);
    }

    /// <summary>
    /// Queues the specified <see cref="Action"/> on update.
    /// </summary>
    /// <param name="updateAction">The update action.</param>
    private void QueueOnUpdate(Action action)
    {
        _dispatchQueue.Enqueue(action);
    }
}
