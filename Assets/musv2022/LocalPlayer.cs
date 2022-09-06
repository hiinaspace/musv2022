using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class LocalPlayer : MonoBehaviour
{
    public Transform camera;
    public AudioSource mic;

    void Start()
    {
        camera = Camera.main.transform;

        var device = Microphone.devices[0];
        Debug.Log($"trying microphone {device}'");
        Microphone.GetDeviceCaps(device, out int minFreq, out int maxFreq);
        var clip = Microphone.Start(device, loop: true, lengthSec: 1, frequency: 48_000);
        Debug.Log($"local mic recording, {minFreq} to {maxFreq}");
        mic.clip = clip;
        mic.loop = true;
        mic.Play();
    }
}
