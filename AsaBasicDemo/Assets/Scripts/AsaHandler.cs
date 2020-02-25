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
        // Init HL tap input
        _gestureRecognizer = new GestureRecognizer();
        _gestureRecognizer.SetRecognizableGestures(GestureSettings.Tap);
        _gestureRecognizer.Tapped += OnTap;
        _gestureRecognizer.StartCapturingGestures();

        InitializeSession();
    }

    void InitializeSession()
    {
        Log("Initializing CloudSpatialAnchorSession.");

        // TODO: Provide your Azure Spatial Anchors AccountId and Primary ASA Key below (ConstantsSecret is not part of the code repo)
        if (String.IsNullOrEmpty(ConstantsSecret.AsaAccountId) || String.IsNullOrEmpty(ConstantsSecret.AsaKey))
        {
            Log("Azure Spatial Anchors Account ID or Account Key are not set.", Color.red);
            return;
        }

        _cloudSpatialAnchorSession = new CloudSpatialAnchorSession();
        _cloudSpatialAnchorSession.Configuration.AccountId = ConstantsSecret.AsaAccountId;
        _cloudSpatialAnchorSession.Configuration.AccountKey = ConstantsSecret.AsaKey;
        _cloudSpatialAnchorSession.SessionUpdated += CloudSpatialAnchorSession_SessionUpdated;
        _cloudSpatialAnchorSession.AnchorLocated += CloudSpatialAnchorSession_AnchorLocated;
        _cloudSpatialAnchorSession.Error += CloudSpatialAnchorSession_Error;
        _cloudSpatialAnchorSession.Start();

        Log("ASA session initialized.\r\n Gaze and tap to place an anchor.");
    }

    public void OnTap(TappedEventArgs tapEvent)
    {
        // Preventing accidental taps
        if (_wasTapped)
        {
            return;
        }
        _wasTapped = true;

        // If no anchor was created before, we are in creation mode
        if (String.IsNullOrEmpty(_currentCloudAnchorId))
        {
            CreateAndSaveAnchor();
        }
        else
        {
            LocalizeAnchor();
        }
    }


    private void CreateAndSaveAnchor()
    {
        Log("Creating new anchor...");

        // Just in case clean up any visuals that have been placed.
        CleanupObjects();

        // Raycast to find a hitpoint where the anchor should be placed
        Ray GazeRay = new Ray(Camera.main.transform.position, Camera.main.transform.forward);
        Physics.Raycast(GazeRay, out RaycastHit hitPoint, float.MaxValue);

        // Instantiate the anchor visual Prefab
        _prefabInstance = Instantiate(Prefab, hitPoint.point, Quaternion.identity) as GameObject;
        var localAnchor = _prefabInstance.AddComponent<WorldAnchor>();
        Log("Created local anchor.");

        // Create CloudSpatialAnchor and link the local anchor
        _currentCloudAnchor = new CloudSpatialAnchor
        {
            LocalAnchor = localAnchor.GetNativeSpatialAnchorPtr()
        };

        _ = Task.Run(async () =>
          {
              // Wait for enough spatial data about the environment so the anchor can be relocalized later
              while (_recommendedSpatialDataForUpload < 1.0F)
              {
                  Log($"Look around to capture enough anchor data: {_recommendedSpatialDataForUpload:P0}", Color.yellow);
                  await Task.Delay(50);
              }

              try
              {
                  Log($"Creating and uploading ASA anchor...", Color.yellow);
                  await _cloudSpatialAnchorSession.CreateAnchorAsync(_currentCloudAnchor);

                  if (_currentCloudAnchor != null)
                  {
                      // Allow the user to tap again to clear the state and enter localization mode
                      _wasTapped = false;
                      _currentCloudAnchorId = _currentCloudAnchor.Identifier;
                      Log($"Saved anchor to Azure Spatial Anchors: {_currentCloudAnchorId}\r\nTap to localize it.", Color.cyan);
                  }
              }
              catch (Exception ex)
              {
                  Log($"Failed to create ASA anchor: {ex.Message}", Color.red);
              }
          });
    }

    public void LocalizeAnchor()
    {
        DispatcherQueue.Enqueue(() =>
        {
            if (_cloudSpatialAnchorSession == null)
            {
                Log("CloudSpatialAnchorSession was null. Weird.", Color.red);
                return;
            }
            else
            {
                // Initialize session fresh & clean
                CleanupObjects();
                _cloudSpatialAnchorSession.Stop();
                _cloudSpatialAnchorSession.Dispose();
                _cloudSpatialAnchorSession = null;
                InitializeSession();

                // Create a Watcher with anchor ID to locate the anchor that was created before
                AnchorLocateCriteria criteria = new AnchorLocateCriteria
                {
                    Identifiers = new string[] { _currentCloudAnchorId }
                };
                _cloudSpatialAnchorSession.CreateWatcher(criteria);

                Log($"Localizing anchor with {_cloudSpatialAnchorSession.GetActiveWatchers().Count} watchers.\r\nLook around to gather spatial data...");
            }
        });
    }

    private void CloudSpatialAnchorSession_AnchorLocated(object sender, AnchorLocatedEventArgs args)
    {
        switch (args.Status)
        {
            case LocateAnchorStatus.Located:
                DispatcherQueue.Enqueue(async () =>
                {
                    // Instantiate the Prefab
                    _prefabInstance = GameObject.Instantiate(Prefab, Vector3.zero, Quaternion.identity) as GameObject;
                    var localAnchor = _prefabInstance.AddComponent<WorldAnchor>();

                    // Get the WorldAnchor from the CloudSpatialAnchor and assign it to the local anchor 
                    localAnchor.SetNativeSpatialAnchorPtr(args.Anchor.LocalAnchor);

                    // Delete the ASA anchor Clean up state so that we can start over and create a new anchor.
                    await _cloudSpatialAnchorSession.DeleteAnchorAsync(args.Anchor);
                    _currentCloudAnchorId = String.Empty;
                    _wasTapped = false;
                    Log($"Anchor located: {args.Identifier}\r\nGaze and tap to place a new anchor.", Color.green);
                    
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

    private void CloudSpatialAnchorSession_SessionUpdated(object sender, SessionUpdatedEventArgs args)
    {
        //    Log($"Look around to capture enough anchor data: {_recommendedSpatialDataForUpload:P0}", Color.yellow);
        _recommendedSpatialDataForUpload = args.Status.RecommendedForCreateProgress;
    }

    private void CloudSpatialAnchorSession_Error(object sender, SessionErrorEventArgs args)
    {
        Log($"ASA Error: {args.ErrorMessage}", Color.red);
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


    public void CleanupObjects()
    {
        if (_prefabInstance != null)
        {
            Destroy(_prefabInstance);
            _prefabInstance = null;
        }

        _currentCloudAnchor = null;
    }

    private void OnDestroy()
    {
        CleanupObjects();
        if (_gestureRecognizer != null)
        {
            if (_gestureRecognizer.IsCapturingGestures())
            {
                _gestureRecognizer.StopCapturingGestures();
            }
            _gestureRecognizer.Dispose();
        }
    }
}