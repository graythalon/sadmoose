import AppKit
import ApplicationServices
import Combine
import CoreGraphics
import CryptoKit
import Foundation
import Network
import Security

@MainActor
final class DriftClient: ObservableObject {
    @Published var status = "Not connected"
    @Published var connected = false
    @Published var connecting = false
    @Published var sharing = false
    @Published var hostSharing = false
    @Published var hostMouseSharing = true
    @Published var hostKeyboardSharing = true
    @Published var permissionMessage = "Grant Accessibility and Input Monitoring access to capture and redirect system input."
    @Published var focusLabel = "The Windows PC is active"

    let peerId = UserDefaults.standard.string(forKey: "drift.peerId") ?? UUID().uuidString
    private var peer: SecurePeerClient?
    private var eventTap: CFMachPort?
    private var tapSource: CFRunLoopSource?
    private var activeTarget = "windows"
    private let injectedMarker: Int64 = 0x44524946544D4143
    private var lastPoint = CGPoint.zero
    private var forwardedKeys = Set<Int>()
    private var heldMouseButtons = Set<String>()

    init() { UserDefaults.standard.set(peerId, forKey: "drift.peerId") }

    var savedHost: String { UserDefaults.standard.string(forKey: "drift.host") ?? "" }
    func savedPairingCode(for host: String) -> String { KeychainStore.load(account: host).map { $0.base64URL } ?? "" }

    func connect(host: String, pairingCode: String) {
        guard let secret = Data(base64URLString: pairingCode) else { status = "Invalid pairing key"; return }
        UserDefaults.standard.set(host, forKey: "drift.host")
        KeychainStore.save(secret, account: host)
        peer?.disconnect()
        let client = SecurePeerClient(host: host, peerId: peerId, displayName: Host.current().localizedName ?? "Mac", secret: secret)
        peer = client
        client.onStatus = { [weak self] value in
            Task { @MainActor in
                self?.status = value
                self?.connected = value == "Connected"
                self?.connecting = value == "Connecting…"
            }
        }
        client.onMessage = { [weak self] message in Task { @MainActor in self?.handle(message) } }
        client.connect()
    }

    func disconnect() { sharing = false; connecting = false; setSharing(false); peer?.disconnect(); peer = nil; connected = false; status = "Not connected" }

    func setSharing(_ enabled: Bool) {
        if !enabled {
            for keyCode in forwardedKeys { peer?.send(["kind": "key", "vk": macToVirtualKey(keyCode), "macKeyCode": keyCode, "down": false]) }
            forwardedKeys.removeAll()
            for button in heldMouseButtons { peer?.send(["kind": "pointer", "action": "button", "button": button, "down": false]) }
            heldMouseButtons.removeAll()
            stopTap()
            return
        }
        guard connected, hostSharing else { stopTap(); sharing = false; return }
        requestPermissions()
        guard AXIsProcessTrusted() else { sharing = false; return }
        if CGPreflightListenEventAccess() == false { permissionMessage = "Turn on Drift under System Settings → Privacy & Security → Input Monitoring, then enable sharing again."; sharing = false; return }
        guard startTap() else { permissionMessage = "Drift could not create an input tap. Check Accessibility and Input Monitoring permissions."; sharing = false; return }
        sharing = true
        permissionMessage = "Input access is enabled. Edge switching is active while sharing is on."
    }

    func requestPermissions() {
        let options = [kAXTrustedCheckOptionPrompt.takeUnretainedValue() as String: true] as CFDictionary
        _ = AXIsProcessTrustedWithOptions(options)
        _ = CGRequestListenEventAccess()
        permissionMessage = "If macOS asks, allow Drift in Accessibility and Input Monitoring, then return here."
        if let url = URL(string: "x-apple.systempreferences:com.apple.preference.security?Privacy_Accessibility") { NSWorkspace.shared.open(url) }
    }

    private func startTap() -> Bool {
        guard eventTap == nil else { return true }
        let types: [CGEventType] = [.keyDown, .keyUp, .flagsChanged, .mouseMoved, .leftMouseDown, .leftMouseUp, .rightMouseDown, .rightMouseUp, .otherMouseDown, .otherMouseUp, .scrollWheel, .leftMouseDragged, .rightMouseDragged, .otherMouseDragged]
        let mask = types.reduce(CGEventMask(0)) { $0 | (CGEventMask(1) << CGEventMask($1.rawValue)) }
        let pointer = Unmanaged.passUnretained(self).toOpaque()
        eventTap = CGEvent.tapCreate(tap: .cgSessionEventTap, place: .headInsertEventTap, options: .defaultTap, eventsOfInterest: CGEventMask(mask), callback: driftEventTapCallback, userInfo: pointer)
        guard let eventTap else { return false }
        tapSource = CFMachPortCreateRunLoopSource(kCFAllocatorDefault, eventTap, 0)
        if let tapSource { CFRunLoopAddSource(CFRunLoopGetMain(), tapSource, .commonModes) }
        CGEvent.tapEnable(tap: eventTap, enable: true)
        return true
    }

    private func stopTap() {
        if let eventTap { CGEvent.tapEnable(tap: eventTap, enable: false) }
        if let tapSource { CFRunLoopRemoveSource(CFRunLoopGetMain(), tapSource, .commonModes) }
        eventTap = nil; tapSource = nil; sharing = false
    }

    fileprivate func handleTap(_ type: CGEventType, event: CGEvent) -> Unmanaged<CGEvent>? {
        if type == .tapDisabledByTimeout || type == .tapDisabledByUserInput {
            if let eventTap { CGEvent.tapEnable(tap: eventTap, enable: true) }
            return Unmanaged.passUnretained(event)
        }
        if event.getIntegerValueField(.eventSourceUserData) == injectedMarker { return Unmanaged.passUnretained(event) }
        guard sharing, connected else { return Unmanaged.passUnretained(event) }
        let point = event.location
        let buttonEvent: (String, Bool)? = switch type {
        case .leftMouseDown: ("left", true)
        case .leftMouseUp: ("left", false)
        case .rightMouseDown: ("right", true)
        case .rightMouseUp: ("right", false)
        case .otherMouseDown: ("middle", true)
        case .otherMouseUp: ("middle", false)
        default: nil
        }
        if let (button, down) = buttonEvent {
            if down { heldMouseButtons.insert(button) } else { heldMouseButtons.remove(button) }
        }
        let isPointerEvent: Bool = switch type {
        case .mouseMoved, .leftMouseDown, .leftMouseUp, .rightMouseDown, .rightMouseUp, .otherMouseDown, .otherMouseUp, .scrollWheel, .leftMouseDragged, .rightMouseDragged, .otherMouseDragged: true
        default: false
        }
        let isKeyboardEvent = type == .keyDown || type == .keyUp || type == .flagsChanged
        if isKeyboardEvent && !hostKeyboardSharing {
            if type == .keyUp { forwardedKeys.remove(Int(event.getIntegerValueField(.keyboardEventKeycode))) }
            if type == .flagsChanged {
                let code = Int(event.getIntegerValueField(.keyboardEventKeycode))
                if modifierIsDown(code, flags: event.flags) == false { forwardedKeys.remove(code) }
            }
            return Unmanaged.passUnretained(event)
        }
        if isPointerEvent && !hostMouseSharing { return Unmanaged.passUnretained(event) }
        if activeTarget == peerId {
            if type == .keyUp { forwardedKeys.remove(Int(event.getIntegerValueField(.keyboardEventKeycode))) }
            if type == .flagsChanged {
                let code = Int(event.getIntegerValueField(.keyboardEventKeycode))
                if modifierIsDown(code, flags: event.flags) == false { forwardedKeys.remove(code) }
            }
            if type == .mouseMoved || type == .leftMouseDragged || type == .rightMouseDragged || type == .otherMouseDragged {
                if heldMouseButtons.isEmpty, let edge = edgeAt(point) { peer?.send(["kind": "edge", "edge": edge]); activeTarget = "windows"; focusLabel = "The Windows PC is active"; return nil }
            }
            return Unmanaged.passUnretained(event)
        }
        switch type {
        case .mouseMoved, .leftMouseDragged, .rightMouseDragged, .otherMouseDragged:
            let dx = Int(point.x - lastPoint.x), dy = Int(point.y - lastPoint.y)
            lastPoint = point
            if dx != 0 || dy != 0 { peer?.send(["kind": "pointer", "action": "move", "dx": dx, "dy": dy]) }
        case .leftMouseDown, .leftMouseUp, .rightMouseDown, .rightMouseUp, .otherMouseDown, .otherMouseUp:
            let button = type == .leftMouseDown || type == .leftMouseUp ? "left" : (type == .rightMouseDown || type == .rightMouseUp ? "right" : "middle")
            peer?.send(["kind": "pointer", "action": "button", "button": button, "down": buttonEvent?.1 ?? false])
        case .scrollWheel:
            peer?.send(["kind": "pointer", "action": "wheel", "wheel": Int(event.getIntegerValueField(.scrollWheelEventDeltaAxis1))])
        case .keyDown, .keyUp, .flagsChanged:
            let macCode = Int(event.getIntegerValueField(.keyboardEventKeycode))
            let virtualKey = macToVirtualKey(macCode)
            if virtualKey == 0 { forwardedKeys.remove(macCode); return Unmanaged.passUnretained(event) }
            if type == .flagsChanged && macCode == 57 {
                peer?.send(["kind": "key", "vk": virtualKey, "macKeyCode": macCode, "down": true])
                peer?.send(["kind": "key", "vk": virtualKey, "macKeyCode": macCode, "down": false])
                return nil
            }
            let down: Bool
            if type == .keyDown { down = true }
            else if type == .keyUp { down = false }
            else { down = modifierIsDown(macCode, flags: event.flags) ?? !forwardedKeys.contains(macCode) }
            if down { forwardedKeys.insert(macCode) } else { forwardedKeys.remove(macCode) }
            peer?.send(["kind": "key", "vk": virtualKey, "macKeyCode": macCode, "down": down])
        default: break
        }
        return nil
    }

    private func handle(_ message: [String: Any]) {
        guard let kind = message["kind"] as? String else { return }
        if kind == "sharing" {
            hostSharing = message["enabled"] as? Bool ?? false
            hostMouseSharing = message["mouse"] as? Bool ?? true
            hostKeyboardSharing = message["keyboard"] as? Bool ?? true
            if !hostSharing { setSharing(false) }
            return
        }
        if kind == "focus" {
            activeTarget = message["target"] as? String ?? "windows"
            lastPoint = CGEvent(source: nil)?.location ?? .zero
            focusLabel = activeTarget == peerId ? "This Mac is active" : "Another device is active"
            if activeTarget == peerId && hostSharing {
                let frame = CGDisplayBounds(CGMainDisplayID())
                let x = frame.minX + CGFloat(message["x"] as? Double ?? 0.5) * frame.width
                let y = frame.minY + CGFloat(message["y"] as? Double ?? 0.5) * frame.height
                let event = CGEvent(mouseEventSource: nil, mouseType: .mouseMoved, mouseCursorPosition: CGPoint(x: x, y: y), mouseButton: .left)
                event?.setIntegerValueField(.eventSourceUserData, value: injectedMarker); event?.post(tap: .cghidEventTap)
            }
            return
        }
        guard hostSharing, kind == "pointer" || kind == "key" || kind == "text" else { return }
        inject(message)
    }

    private func inject(_ message: [String: Any]) {
        guard let kind = message["kind"] as? String else { return }
        if kind == "pointer" {
            let action = message["action"] as? String ?? ""
            if action == "move" {
                let current = CGEvent(source: nil)?.location ?? .zero
                let dx = message["dx"] as? Int ?? 0, dy = message["dy"] as? Int ?? 0
                let next = CGPoint(x: current.x + CGFloat(dx), y: current.y + CGFloat(dy))
                postMouse(.mouseMoved, point: next)
            if activeTarget == peerId, heldMouseButtons.isEmpty, let edge = edgeAt(next) {
                    peer?.send(["kind": "edge", "edge": edge])
                    activeTarget = "windows"
                    focusLabel = "The Windows PC is active"
                }
            } else if action == "button" {
                let button = message["button"] as? String ?? "left", down = message["down"] as? Bool ?? false
                if down { heldMouseButtons.insert(button) } else { heldMouseButtons.remove(button) }
                let type: CGEventType = button == "left" ? (down ? .leftMouseDown : .leftMouseUp) : (button == "right" ? (down ? .rightMouseDown : .rightMouseUp) : (down ? .otherMouseDown : .otherMouseUp))
                postMouse(type, point: CGEvent(source: nil)?.location ?? .zero, button: button == "right" ? .right : (button == "middle" ? .center : .left))
            } else if action == "wheel" {
                let event = CGEvent(scrollWheelEvent2Source: nil, units: .line, wheelCount: 1, wheel1: Int32(message["wheel"] as? Int ?? 0), wheel2: 0, wheel3: 0)
                event?.setIntegerValueField(.eventSourceUserData, value: injectedMarker); event?.post(tap: .cghidEventTap)
            }
        } else if kind == "key" {
            let vk = message["vk"] as? Int ?? androidToVirtualKey(message["keyCode"] as? Int ?? 0)
            let code = virtualToMacKey(vk)
            if code == 0 { return }
            let event = CGEvent(keyboardEventSource: nil, virtualKey: CGKeyCode(code), keyDown: message["down"] as? Bool ?? false)
            event?.setIntegerValueField(.eventSourceUserData, value: injectedMarker); event?.post(tap: .cghidEventTap)
        } else if kind == "text", let text = message["text"] as? String {
            for scalar in text.utf16 {
                var unit = scalar
                let event = CGEvent(keyboardEventSource: nil, virtualKey: 0, keyDown: true)
                event?.keyboardSetUnicodeString(stringLength: 1, unicodeString: &unit)
                event?.setIntegerValueField(.eventSourceUserData, value: injectedMarker); event?.post(tap: .cghidEventTap)
                let up = CGEvent(keyboardEventSource: nil, virtualKey: 0, keyDown: false)
                up?.setIntegerValueField(.eventSourceUserData, value: injectedMarker); up?.post(tap: .cghidEventTap)
            }
        }
    }

    private func postMouse(_ type: CGEventType, point: CGPoint, button: CGMouseButton = .left) {
        let event = CGEvent(mouseEventSource: nil, mouseType: type, mouseCursorPosition: point, mouseButton: button)
        event?.setIntegerValueField(.eventSourceUserData, value: injectedMarker); event?.post(tap: .cghidEventTap)
    }

    private func edgeAt(_ point: CGPoint) -> String? {
        let frame = CGDisplayBounds(CGMainDisplayID())
        if point.x <= frame.minX + 1 { return "left" }
        if point.x >= frame.maxX - 1 { return "right" }
        if point.y <= frame.minY + 1 { return "top" }
        if point.y >= frame.maxY - 1 { return "bottom" }
        return nil
    }
}

private let driftEventTapCallback: CGEventTapCallBack = { _, type, event, userInfo in
    guard let userInfo else { return Unmanaged.passUnretained(event) }
    let client = Unmanaged<DriftClient>.fromOpaque(userInfo).takeUnretainedValue()
    return MainActor.assumeIsolated { client.handleTap(type, event: event) }
}

private func macToVirtualKey(_ code: Int) -> Int {
    let map: [Int: Int] = [0:65, 1:83, 2:68, 3:70, 4:72, 5:71, 6:90, 7:88, 8:67, 9:86, 11:66, 12:81, 13:87, 14:69, 15:82, 16:89, 17:84, 31:79, 32:85, 34:73, 35:80, 37:76, 38:74, 40:75, 45:78, 46:77, 18:49, 19:50, 20:51, 21:52, 23:53, 22:54, 26:55, 28:56, 25:57, 29:48, 36:13, 48:9, 49:32, 51:8, 53:27, 24:0xBB, 27:0xBD, 30:0xDD, 33:0xDB, 39:0xDE, 41:0xBA, 42:0xDC, 43:0xBC, 44:0xBF, 47:0xBE, 50:0xC0, 57:0x14, 117:0x2E, 115:0x24, 119:0x23, 116:0x21, 121:0x22, 122:0x70, 120:0x71, 99:0x72, 118:0x73, 96:0x74, 97:0x75, 98:0x76, 100:0x77, 101:0x78, 109:0x79, 103:0x7A, 111:0x7B, 123:37, 124:39, 125:40, 126:38, 56:16, 60:16, 59:17, 62:17, 58:18, 61:18, 55:91, 54:92]
    return map[code] ?? 0
}

private func modifierIsDown(_ code: Int, flags: CGEventFlags) -> Bool? {
    switch code {
    case 56, 60: return flags.contains(.maskShift)
    case 59, 62: return flags.contains(.maskControl)
    case 58, 61: return flags.contains(.maskAlternate)
    case 54, 55: return flags.contains(.maskCommand)
    case 57: return flags.contains(.maskAlphaShift)
    default: return nil
    }
}

private func virtualToMacKey(_ vk: Int) -> Int {
    let pairs = [65:0,83:1,68:2,70:3,72:4,71:5,90:6,88:7,67:8,86:9,66:11,81:12,87:13,69:14,82:15,89:16,84:17,79:31,85:32,73:34,80:35,76:37,74:38,75:40,78:45,77:46,13:36,9:48,32:49,8:51,27:53,0xBB:24,0xBD:27,0xDD:30,0xDB:33,0xDE:39,0xBA:41,0xDC:42,0xBC:43,0xBF:44,0xBE:47,0xC0:50,0x14:57,0x2E:117,0x24:115,0x23:119,0x21:116,0x22:121,0x70:122,0x71:120,0x72:99,0x73:118,0x74:96,0x75:97,0x76:98,0x77:100,0x78:101,0x79:109,0x7A:103,0x7B:111,37:123,39:124,40:125,38:126,16:56,17:59,18:58,91:55,92:54]
    return pairs[vk] ?? 0
}

private func androidToVirtualKey(_ keyCode: Int) -> Int {
    if (29...54).contains(keyCode) { return 65 + keyCode - 29 }
    if (7...16).contains(keyCode) { return keyCode == 7 ? 48 : 49 + keyCode - 8 }
    let map = [66:13, 61:9, 67:8, 112:0x2E, 111:27, 21:37, 19:38, 22:39, 20:40, 74:0xBA, 70:0xBB, 55:0xBC, 69:0xBD, 56:0xBE, 76:0xBF, 68:0xC0, 71:0xDB, 73:0xDC, 72:0xDD, 75:0xDE, 59:16, 60:16, 113:17, 114:17, 57:18, 58:18, 115:0x14, 117:91, 118:92, 62:32, 122:0x24, 123:0x23, 92:0x21, 93:0x22, 131:0x70, 132:0x71, 133:0x72, 134:0x73, 135:0x74, 136:0x75, 137:0x76, 138:0x77, 139:0x78, 140:0x79, 141:0x7A, 142:0x7B, 4:27]
    return map[keyCode] ?? 0
}

private extension Data {
    init?(base64URLString: String) {
        var value = base64URLString.replacingOccurrences(of: "-", with: "+").replacingOccurrences(of: "_", with: "/")
        value += String(repeating: "=", count: (4 - value.count % 4) % 4)
        self.init(base64Encoded: value)
        if self.count != 32 { return nil }
    }
    var base64URL: String { base64EncodedString().replacingOccurrences(of: "+", with: "-").replacingOccurrences(of: "/", with: "_").replacingOccurrences(of: "=", with: "") }
}

private enum KeychainStore {
    private static let service = "com.graythalon.drift.pairing"
    static func save(_ data: Data, account: String) {
        let query: [String: Any] = [kSecClass as String: kSecClassGenericPassword, kSecAttrService as String: service, kSecAttrAccount as String: account, kSecUseDataProtectionKeychain as String: true]
        SecItemDelete(query as CFDictionary)
        var item = query
        item[kSecValueData as String] = data
        item[kSecAttrAccessible as String] = kSecAttrAccessibleAfterFirstUnlockThisDeviceOnly
        SecItemAdd(item as CFDictionary, nil)
    }
    static func load(account: String) -> Data? {
        let query: [String: Any] = [kSecClass as String: kSecClassGenericPassword, kSecAttrService as String: service, kSecAttrAccount as String: account, kSecReturnData as String: true, kSecMatchLimit as String: kSecMatchLimitOne, kSecUseDataProtectionKeychain as String: true]
        var result: CFTypeRef?
        guard SecItemCopyMatching(query as CFDictionary, &result) == errSecSuccess else { return nil }
        return result as? Data
    }
}

private final class SecurePeerClient {
    private let host: String
    private let peerId: String
    private let displayName: String
    private let secret: Data
    private let queue = DispatchQueue(label: "drift.peer.client")
    private var connection: NWConnection?
    private var inputBuffer = Data()
    private var sendKey: SymmetricKey?
    private var receiveKey: SymmetricKey?
    private var sendCounter: UInt64 = 0
    private var receiveCounter: UInt64 = 0
    private var nonceSaltClient = Data()
    var onStatus: ((String) -> Void)?
    var onMessage: (([String: Any]) -> Void)?

    init(host: String, peerId: String, displayName: String, secret: Data) { self.host = host; self.peerId = peerId; self.displayName = displayName; self.secret = secret }
    func connect() {
        guard let port = NWEndpoint.Port(rawValue: 42857) else { return }
        connection = NWConnection(host: NWEndpoint.Host(host), port: port, using: .tcp)
        connection?.stateUpdateHandler = { [weak self] state in
            guard let self else { return }
            if case .ready = state { self.beginHandshake() }
            if case .failed(let error) = state { self.onStatus?("Connection failed: \(error.localizedDescription)") }
        }
        connection?.start(queue: queue)
        onStatus?("Connecting…")
    }
    func disconnect() { connection?.cancel(); connection = nil; onStatus?("Not connected") }
    func send(_ message: [String: Any]) {
        queue.async { [weak self] in guard let self, self.sendKey != nil else { return }; self.sendEncrypted(message) }
    }
    private func beginHandshake() {
        let clientNonce = Data((0..<32).map { _ in UInt8.random(in: 0...255) })
        nonceSaltClient = clientNonce
        let nonceText = clientNonce.base64URL
        let proof = Data(HMAC<SHA256>.authenticationCode(for: Data("hello\n\(peerId)\n\(nonceText)".utf8), using: SymmetricKey(data: secret))).base64URL
        sendLine(["type":"hello", "peerId":peerId, "displayName":displayName, "clientNonce":nonceText, "proof":proof])
        receiveLine()
    }
    private func receiveLine() {
        connection?.receive(minimumIncompleteLength: 1, maximumLength: 65536) { [weak self] data, _, complete, error in
            guard let self else { return }
            if let data { self.inputBuffer.append(data) }
            while let newline = self.inputBuffer.firstIndex(of: 10) {
                let line = self.inputBuffer.prefix(upTo: newline)
                self.inputBuffer.removeSubrange(...newline)
                self.handleLine(Data(line))
            }
            if error != nil || complete { self.onStatus?("Disconnected"); return }
            self.receiveLine()
        }
    }
    private func handleLine(_ line: Data) {
        guard let object = (try? JSONSerialization.jsonObject(with: line)) as? [String: Any] else { return }
        if sendKey == nil {
            guard object["type"] as? String == "welcome", let serverNonceText = object["serverNonce"] as? String,
                  let proofText = object["proof"] as? String, let serverNonce = Data(base64URLString: serverNonceText),
                  let proof = Data(base64URLString: proofText) else { onStatus?("Pairing rejected"); return }
            let expected = Data(HMAC<SHA256>.authenticationCode(for: Data("welcome\n\(peerId)\n\(nonceSaltClient.base64URL)\n\(serverNonceText)".utf8), using: SymmetricKey(data: secret)))
            guard expected == proof else { onStatus?("Pairing key rejected"); return }
            let salt = nonceSaltClient + serverNonce
            sendKey = HKDF<SHA256>.deriveKey(inputKeyMaterial: SymmetricKey(data: secret), salt: salt, info: Data("drift-v1/client-to-server".utf8), outputByteCount: 32)
            receiveKey = HKDF<SHA256>.deriveKey(inputKeyMaterial: SymmetricKey(data: secret), salt: salt, info: Data("drift-v1/server-to-client".utf8), outputByteCount: 32)
            onStatus?("Connected"); return
        }
        guard let counter = object["counter"] as? UInt64, counter == receiveCounter + 1,
              let cipherText = object["ciphertext"] as? String, let cipher = Data(base64Encoded: cipherText),
              let tagText = object["tag"] as? String, let tag = Data(base64Encoded: tagText), let key = receiveKey else { onStatus?("Invalid secure frame"); return }
        let nonce = makeNonce(counter)
        guard let box = try? AES.GCM.SealedBox(nonce: AES.GCM.Nonce(data: nonce), ciphertext: cipher, tag: tag),
              let clear = try? AES.GCM.open(box, using: key, authenticating: nonce),
              let message = (try? JSONSerialization.jsonObject(with: clear)) as? [String: Any] else { onStatus?("Secure frame could not be verified"); return }
        receiveCounter = counter; onMessage?(message)
    }
    private func sendEncrypted(_ message: [String: Any]) {
        guard let key = sendKey, let clear = try? JSONSerialization.data(withJSONObject: message) else { return }
        sendCounter += 1
        let nonce = makeNonce(sendCounter)
        guard let box = try? AES.GCM.seal(clear, using: key, nonce: AES.GCM.Nonce(data: nonce), authenticating: nonce) else { return }
        sendLine(["counter":sendCounter, "ciphertext":box.ciphertext.base64EncodedString(), "tag":box.tag.base64EncodedString()])
    }
    private func makeNonce(_ counter: UInt64) -> Data {
        var result = Data("DRFT".utf8)
        var big = counter.bigEndian
        withUnsafeBytes(of: &big) { result.append(contentsOf: $0) }
        return result
    }
    private func sendLine(_ object: [String: Any]) {
        guard let data = try? JSONSerialization.data(withJSONObject: object) else { return }
        var line = data; line.append(10)
        connection?.send(content: line, completion: .contentProcessed { [weak self] error in if let error { self?.onStatus?("Send failed: \(error.localizedDescription)") } })
    }
}
