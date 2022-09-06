const myId = crypto.randomUUID();
const activeConnections = new Map();
const channels = new Map();
const lastDataMessage = new Map();
const makingOffer = new Set();
const ignoreOffer = new Set();

let socket = new WebSocket('wss://c.hiina.space/ws')
socket.addEventListener('close', console.error)
socket.addEventListener('open', (e) => {
  console.log("open", e)
  sendHi()
})


const statusDisplay = document.getElementById('status')
function updateStatus() {
  statusDisplay.textContent = `socket: ${socket.readyState}\n`;
  for (let [peerId, pc] of activeConnections) {
    let d = document.getElementById(peerId)
    let p = d.querySelector('pre')
    let c = channels.get(peerId)
    p.textContent = `signaling ${pc.signalingState} ice ${pc.iceGatheringState} channel: ${c?.readyState}`
    let m = d.querySelector('p')
    m.textContent = lastDataMessage.get(peerId)
  }

  setTimeout(updateStatus, 2000)
}
updateStatus()

function sendHi() {
  socket.send(JSON.stringify({ from: myId, type: 'hi' }))
  // send every so often
  setTimeout(sendHi, 5000)
  // TODO send PlayerState thing
  //for (let c of channels.values()) {
  //  if (c.readyState === 'open') {
  //    c.send(`hi, ${Math.random()}`)
  //  }
  //}
}

socket.addEventListener('message', onMessage)
async function onMessage(e) {
  var msg
  try {
    msg = JSON.parse(e.data)
  } catch (e) {
    console.error(e)
    return
  }

  if (msg.type == 'hi') {
    if (msg.from < myId &&
      !activeConnections.has(msg.from)) {
      console.log(`new hi from lower ${msg.from}, sending initial offer`)

      let pc = startConnection(msg.from)
      let c = pc.createDataChannel("channel")
      initChannel(msg.from, c, pc)

      // create offer and send
      await pc.setLocalDescription(await pc.createOffer());
      sendSdp(msg.from, pc)
    }
  } else if (msg.to === myId) {
    // existing or create
    let pc = activeConnections.get(msg.from) || startConnection(msg.from)

    if (msg.type == 'new-ice-candidate') {
      // we offered earlier and set local desc, they responded
      console.log(`got ice candidate from ${msg.from}`)
      try {
        pc.addIceCandidate(msg.candidate).catch(console.error)
      } catch (e) {
        // swallow if ignoring
        if (!ignoreOffer.get(peerId)) throw e
      }
    } else if (msg.type == 'sdp') {
      // https://developer.mozilla.org/en-US/docs/Web/API/WebRTC_API/Perfect_negotiation
      const offerCollision = (msg.sdp.type === 'offer') &&
        (makingOffer.has(msg.from) || pc.signalingState !== "stable")

      const polite = msg.from > myId // lesser peer is polite
      const ignore = !polite && offerCollision

      if (ignore) {
        // so we can ignore ice message errors later
        ignoreOffer.add(msg.from)
        console.log(`got sdp from already connected ${msg.from} but not polite and offer collision, ignoring`)
        return
      } else {
        ignoreOffer.delete(msg.from)
      }

      // works whether it's an offer or answer
      await pc.setRemoteDescription(msg.sdp)

      if (msg.sdp.type === "offer") {
        console.log(`got ${msg.sdp.type} from ${msg.from}`)
        // empty params apparently does the right thing
        await pc.setLocalDescription()
        sendSdp(msg.from, pc)
      }
    }
  }
}

function sendSdp(peerId, pc) {
  socket.send(JSON.stringify({
    from: myId,
    to: peerId,
    type: "sdp",
    sdp: pc.localDescription
  }))
}

function initChannel(peerId, channel, pc) {
  channels.set(peerId, channel)
  channel.onopen = (e) => {
    console.log(`channel for ${peerId} opened`, e)
  }
  channel.onclose = (e) => {
    console.log(`channel for ${peerId} closed, might as well clean up the connection`, e)
    cleanupConnection(peerId, pc)
  }
  channel.onmessage = (e) => {
    //console.log("new message", e)
    lastDataMessage.set(peerId, e.data)
  }
}

function cleanupConnection(peerId, pc) {
  pc.close()
  activeConnections.delete(peerId)
  channels.delete(peerId)
  makingOffer.delete(peerId)
  ignoreOffer.delete(peerId)
  var d = document.getElementById(peerId)
  if (d) {
    d.remove()
  }
}

function startConnection(peerId) {
  makeRemotePlayer(peerId)

  let pc = new RTCPeerConnection({
    iceServers: [
      {
        urls: "stun:stun3.l.google.com:19302"
      }
    ]
  });

  // no await, assume renegotiation will happen once added
  getMedia(pc)

  activeConnections.set(peerId, pc);

  pc.onconnectionstatechange = console.log
  // https://developer.mozilla.org/en-US/docs/Web/API/WebRTC_API/Perfect_negotiation
  pc.onnegotiationneeded = async (e) => {
    console.log(`negotation needed with ${peerId}, sending offer`)
    try {
      // state for the perfect negotiation thing
      makingOffer.add(peerId)
      await pc.setLocalDescription()
      sendSdp(peerId, pc)
    } finally {
      makingOffer.delete(peerId)
    }
  }
  pc.onsignalingstatechange = (e) => {
    console.log(`state ${pc.signalingState} for ${peerId}`)

    if (pc.signalingState == "closed") {
      console.log(`${peerId} disconnected, close up this connection`)
      cleanupConnection(peerId, pc)
    }
  }
  pc.oniceconnectionstatechange = (e) => {
    console.log(`new ice connection state ${peerId} ${pc.iceConnectionState}`)
    if (pc.iceConnectionState === "failed") {
      pc.restartIce()
    }
    if (pc.iceConnectionState == "disconnected") {
      console.log(`${peerId} disconnected, close up this connection`)
      cleanupConnection(peerId, pc)
    }
  }
  pc.onicecandidate = (e) => {
    if (e.candidate) {
      console.log(`got local ice candidate, sending to ${peerId}`)
      socket.send(JSON.stringify({
        type: "new-ice-candidate",
        from: myId,
        to: peerId,
        candidate: e.candidate
      }));
    }
  }
  pc.ondatachannel = (e) => {
    console.log(`got channel from ${peerId}`, e)
    initChannel(peerId, e.channel, pc)
  }
  pc.ontrack = (e) => {
    console.log('on track', e)
    var a = document.createElement("audio");
    a.controls = true // makes it visible?
    a.srcObject = e.streams[0];
    a.play()
    var d = document.getElementById(peerId)
    d.appendChild(a)
  }

  return pc
}

function makeRemotePlayer(peerId) {
  let d = document.createElement('div')
  d.id = peerId
  let h = document.createElement('h2')
  h.textContent = `peer ${peerId}`
  let p = document.createElement('pre')
  let m = document.createElement('p')
  d.append(h, p, m)
  document.body.append(d)
}

async function getMedia(pc) {
  try {
    let stream = await navigator.mediaDevices.getUserMedia({ audio: true });
    // attach to peer connection
    stream.getTracks().forEach((t) => pc.addTrack(t, stream))
  } catch (err) {
    console.error(err)
  }
}
