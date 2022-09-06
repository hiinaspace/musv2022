# Multiplayer Unity Scene Viewer (MUSV) 2022

very rough proof of concept of multiplayer in unity using WebRTC for
(ostensibly) robust voice chat and a relatively convenient SCTP networking
stack (DataChannel). Connections are currently full-mesh so it probably won't
scale past like 10 people.

The signaling server itself is a stupid broadcast channel through websocat:

    websocat -t ws-l:127.0.0.1:3012 broadcast:mirror

proxied through nginx for TLS (which you probably need).

I have rough plans to switch to the livekit SFU, which also has a useful "room"
concept. Will take some doing however, as their official SDK only works with
unity webGL, not native unity.

# Web Version

there's a compatible plain javascript client for the same networking in `webversion`,
which is mostly the MDN webrtc signaling tutorial, but using that broadcast signaling
instead of anything more intelligent.

## Building locally

Some things the unity package manager should download for you after cloning the project.
I think you'll manually need to add the following:

- Steam Audio
- OVRLipSync
- Alicia.vrm
