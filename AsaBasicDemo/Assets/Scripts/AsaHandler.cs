using Microsoft.Azure.SpatialAnchors;
using System;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.XR.WSA;
using UnityEngine.XR.WSA.Input;

public class AsaHandler : MonoBehaviour
{
    public TextMesh LogText;
    public GameObject Prefab;

    private GameObject _prefabInstance;

    private GestureRecognizer _gestureRecognizer;
    private bool _wasTapped = false;

    private CloudSpatialAnchorSession _cloudSpatialAnchorSession;
    private CloudSpatialAnchor _currentCloudAnchor;
    private string _currentCloudAnchorId = String.Empty;
    private float _recommendedSpatialDataForUpload = 0;


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

        if (String.IsNullOrEmpty(_currentCloudAnchorId))
        {
            Log("Creating new anchor...");

            // Just in case clean up any anchors that have been placed.
            CleanupObjects();

            // Raycast to find a hitpoint where the anchor should be placed
            Ray GazeRay = new Ray(tapEvent.headPose.position, tapEvent.headPose.forward);
            Physics.Raycast(GazeRay, out RaycastHit hitInfo, float.MaxValue);

            CreateAndSaveAnchor(hitInfo.point);
        }
        else
        {
            ResetSession(() =>
            {
                InitializeSession();

                // Create a Watcher to look for the anchor we created.
                AnchorLocateCriteria criteria = new AnchorLocateCriteria
                {
                    Identifiers = new string[] { _currentCloudAnchorId }
                };
                _cloudSpatialAnchorSession.CreateWatcher(criteria);

                Log($"{_cloudSpatialAnchorSession.GetActiveWatchers().Count} watchers created. Localizing anchor.\r\nLook around to gather spatial data...");
            });
        }
    }

    /// <summary>
    /// Creates a sphere at the hit point, and then saves a CloudSpatialAnchor there.
    /// </summary>
    /// <param name="hitPoint">The hit point.</param>
    private void CreateAndSaveAnchor(Vector3 hitPoint)
    {
        // Instantiate the 
        _prefabInstance = GameObject.Instantiate(Prefab, hitPoint, Quaternion.identity) as GameObject;
        var localAnchor = _prefabInstance.AddComponent<WorldAnchor>();
        Log("Created local anchor.");

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
                Log($"Look around to capture enough anchor data: {_recommendedSpatialDataForUpload:P0}", Color.yellow);
                await Task.Delay(100);
            }

            try
            {
                Log($"Creating and uploading ASA anchor...", Color.yellow);
                await _cloudSpatialAnchorSession.CreateAnchorAsync(_currentCloudAnchor);

                if (_currentCloudAnchor != null)
                {
                    // Allow the user to tap again to clear state and look for the anchor.
                    _wasTapped = false;

                    _currentCloudAnchorId = _currentCloudAnchor.Identifier;
                    Log($"Saved anchor to Azure Spatial Anchors: {_currentCloudAnchorId}\r\nTap to localize it.", Color.cyan);
                }
                else
                {
                    Log("Failed to create ASA anchor but no exception was thrown.", Color.red);
                }
            }
            catch (Exception ex)
            {
                Log($"Failed to create ASA anchor: {ex.Message}", Color.red);
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

        _cloudSpatialAnchorSession.LogLevel = SessionLogLevel.Warning;

        _cloudSpatialAnchorSession.Error += CloudSpatialAnchorSession_Error;
        _cloudSpatialAnchorSession.OnLogDebug += CloudSpatialAnchorSession_OnLogDebug;
        _cloudSpatialAnchorSession.SessionUpdated += CloudSpatialAnchorSession_SessionUpdated;

        _cloudSpatialAnchorSession.Start();

        Log("ASA session initialized.\r\n Gaze and tap to place an anchor.");
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

        _currentCloudAnchor = null;
    }

    /// <summary>
    /// Cleans up objects and stops the CloudSpatialAnchorSessions.
    /// </summary>
    public void ResetSession(Action completionRoutine = null)
    {
        Log("Resetting the session...");

        if (_cloudSpatialAnchorSession.GetActiveWatchers().Count > 0)
        {
            Log("We are resetting the session with active watchers, which is unexpected.", Color.red);
        }

        CleanupObjects();

        _cloudSpatialAnchorSession.Reset();

        DispatcherQueue.Enqueue(() =>
        {
            if (_cloudSpatialAnchorSession != null)
            {
                _cloudSpatialAnchorSession.Stop();
                _cloudSpatialAnchorSession.Dispose();
                Log("ASA session reset.");
                completionRoutine?.Invoke();
            }
            else
            {
                Log("CloudSpatialAnchorSession was null, which is unexpected.", Color.red);
            }
        });
    }

    private void CloudSpatialAnchorSession_Error(object sender, SessionErrorEventArgs args)
    {
        Log($"ASA Error: {args.ErrorMessage}", Color.red);
    }

    private void CloudSpatialAnchorSession_OnLogDebug(object sender, OnLogDebugEventArgs args)
    {
        //  Log("ASA Log: " + args.Message);
    }

    private void CloudSpatialAnchorSession_SessionUpdated(object sender, SessionUpdatedEventArgs args)
    {
    //    Log($"Look around to capture enough anchor data: {_recommendedSpatialDataForUpload:P0}", Color.yellow);
        _recommendedSpatialDataForUpload = args.Status.RecommendedForCreateProgress;
    }

    private void CloudSpatialAnchorSession_AnchorLocated(object sender, AnchorLocatedEventArgs args)
    {
        switch (args.Status)
        {
            case LocateAnchorStatus.Located:
                Log($"Anchor located: {args.Identifier}\r\nTap to start over.", Color.green);
                DispatcherQueue.Enqueue(() =>
                {
                    // Create a green sphere.
                    _prefabInstance = GameObject.Instantiate(Prefab, Vector3.zero, Quaternion.identity) as GameObject;
                    var localAnchor = _prefabInstance.AddComponent<WorldAnchor>();

                    // Get the WorldAnchor from the CloudSpatialAnchor and assign it to the local anchor 
                    localAnchor.SetNativeSpatialAnchorPtr(args.Anchor.LocalAnchor);

                    // Clean up state so that we can start over and create a new anchor.
                    _currentCloudAnchorId = String.Empty;
                    _wasTapped = false;
                });
                break;
            case LocateAnchorStatus.AlreadyTracked:
                Log($"ASA Anchor already tracked: {args.Identifier}", Color.magenta);
                break;
            case LocateAnchorStatus.NotLocated:
                Log($"ASA Anchor not located : {args.Identifier}", Color.magenta);
                break;
            case LocateAnchorStatus.NotLocatedAnchorDoesNotExist:
                Log($"ASA Anchor not located -> Does not exist: {args.Identifier}", Color.red);
                break;
        }
    }

    private void CloudSpatialAnchorSession_LocateAnchorsCompleted(object sender, LocateAnchorsCompletedEventArgs args)
    {
 //       Log($"ASA locating anchors completed. Watcher identifier: {args.Watcher.Identifier}");
    }

    private void Update()
    {
        DispatcherQueue.DequeueAndExecute();
    }

    private void Log(string text, Color? color = null)
    {
        if (LogText != null)
        {
            DispatcherQueue.Enqueue(() =>
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
}