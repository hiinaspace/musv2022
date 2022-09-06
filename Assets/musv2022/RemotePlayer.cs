using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using Newtonsoft.Json;
using Unity.WebRTC;
using UnityEngine;


/// <summary>
/// drives the voice chat, text chat, and pose for each remote player.
/// Instantiated on new connection from NetworkManager.
/// </summary>
public class RemotePlayer : MonoBehaviour
{
    // avatar head
    public Transform head;
    public AudioSource audioSource;
    private RTCDataChannel channel;
    private RTCPeerConnection connection;
    private string peerId;
    private LocalPlayer localPlayer;

    private MediaStream mediaStream;

    public void InitConnection(string peerId, RTCPeerConnection pc, bool makeChannel)
    {
        localPlayer = FindObjectOfType<LocalPlayer>();

        this.peerId = peerId;
        connection = pc;

        // high peer will set up data channel;
        if (makeChannel)
        {
            channel = pc.CreateDataChannel("channel");
            initChannel();
        }
        else
        {
            pc.OnDataChannel += (chan) =>
            {
                Debug.Log($"got dataChannel for {this.peerId}");
                channel = chan;
                initChannel();
            };
        }

        // not sure if the input stream is necessary...
        var inputStream = new MediaStream();
        var inputTrack = new AudioStreamTrack(localPlayer.mic);
        pc.AddTrack(inputTrack, inputStream);
        Debug.Log($"sending input mic to remote {inputTrack}");

        mediaStream = new MediaStream();
        mediaStream.OnAddTrack += (e) =>
        {
            Debug.Log($"got remote audio track, playing through audiosource");
            var track = e.Track as AudioStreamTrack;
            // some weird native thing that bypasses a regular "clip".
            // can't seem to mute or otherwise affect the mix.
            audioSource.SetTrack(track);
            audioSource.loop = true;
            Debug.Log($"set remote audio track, what's on the audioSource: {audioSource.clip}");
            audioSource.Play();
        };
        // kind of weird api.
        pc.OnTrack += (e) =>
        {
            mediaStream.AddTrack(e.Track);
        };
    }

    private void initChannel()
    {
        channel.OnMessage += OnMessage;
        // TODO with reference to localPlayer, periodically send position
        // and like the contents of a local text field.
        InvokeRepeating(nameof(SendState), 1f, 0.05f);
    }

    class PlayerState
    {
        public UnityEngine.Vector3 position;
        public Quaternion rotation;
        public Quaternion headRotation;
        public string someText;
    }

    private void SendState()
    {
        if (channel.ReadyState != RTCDataChannelState.Open) return;

        channel.Send(JsonUtility.ToJson(new PlayerState
        {
            position = localPlayer.transform.position,
            rotation = localPlayer.transform.rotation,
            headRotation = localPlayer.camera.rotation,
            // TODO some text chat
            someText = "hi"
        }));
    }

    private void OnMessage(byte[] data)
    {
        var text = Encoding.UTF8.GetString(data);
        //Debug.Log($"got message {text}");

        // get player state update
        try
        {
            var state = JsonUtility.FromJson<PlayerState>(text);
            transform.SetPositionAndRotation(state.position, state.rotation);
            head.rotation = state.headRotation;
            // TODO the text thing
        }
        catch (Exception e)
        {
            Debug.LogException(e);
        }
    }

    void Update()
    {

    }

    void OnDestroy()
    {
        try
        {
            connection.Close();
        }
        catch (Exception e)
        {
            // oh well
        }
    }
}
