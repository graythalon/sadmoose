import SwiftUI

@main
struct DriftMacApp: App {
    @StateObject private var model = DriftClient()
    var body: some Scene {
        WindowGroup {
            ContentView().environmentObject(model).frame(minWidth: 560, minHeight: 560)
        }
        .windowResizability(.contentSize)
    }
}

struct ContentView: View {
    @EnvironmentObject private var model: DriftClient
    @State private var host = ""
    @State private var pairingCode = ""

    var body: some View {
        VStack(alignment: .leading, spacing: 18) {
            HStack(alignment: .top) {
                VStack(alignment: .leading, spacing: 6) {
                    Text("DRIFT  /  MAC CLIENT").font(.system(size: 10, weight: .bold, design: .rounded)).tracking(1.4).foregroundStyle(Color(red: 0.78, green: 0.95, blue: 0.42))
                    Text("One pointer. Every screen.").font(.system(size: 26, weight: .semibold, design: .rounded))
                }
                Spacer()
                Text(model.status).font(.system(size: 11, weight: .medium)).foregroundStyle(model.connected ? .green : .secondary).padding(.horizontal, 11).padding(.vertical, 7).background(.quaternary, in: Capsule())
            }
            GroupBox("Connect to your Windows hub") {
                VStack(alignment: .leading, spacing: 10) {
                    TextField("PC address, e.g. 192.168.1.25", text: $host).textFieldStyle(.roundedBorder)
                    SecureField("Pairing key", text: $pairingCode).textFieldStyle(.roundedBorder)
                    HStack {
                        Button(model.connecting ? "Cancel" : (model.connected ? "Disconnect" : "Connect")) {
                            if model.connected || model.connecting { model.disconnect() } else { model.connect(host: host, pairingCode: pairingCode) }
                        }.buttonStyle(.borderedProminent).tint(Color(red: 0.78, green: 0.95, blue: 0.42)).foregroundStyle(.black)
                        .disabled(host.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty || pairingCode.isEmpty)
                        Button("Input permissions") { model.requestPermissions() }
                        Spacer()
                    }
                }.padding(.vertical, 4)
            }
            GroupBox("Input on this Mac") {
                VStack(alignment: .leading, spacing: 10) {
                    Toggle("Send this Mac’s keyboard and pointer to other screens", isOn: $model.sharing).disabled(!model.connected || !model.hostSharing).onChange(of: model.sharing) { _, value in model.setSharing(value) }
                    Text(model.permissionMessage).font(.system(size: 11)).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
                }.padding(.vertical, 4)
            }
            GroupBox("Desk status") {
                VStack(alignment: .leading, spacing: 9) {
                    Label(model.focusLabel, systemImage: "cursorarrow.motionlines")
                    Text("When another screen owns the pointer, mouse and key events from this Mac are forwarded to the active device. Move to an edge on this Mac to return control to the PC.")
                        .font(.system(size: 11)).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
                    Text("Android uses its Accessibility Service for pointer gestures and Drift Keyboard for text input.")
                        .font(.system(size: 10)).foregroundStyle(.tertiary).fixedSize(horizontal: false, vertical: true)
                }.padding(.vertical, 4)
            }
            Spacer(minLength: 0)
            Text("Encrypted local connection · Pairing key is never sent in the clear").font(.system(size: 10)).foregroundStyle(.tertiary)
        }
        .padding(24)
        .background(Color(nsColor: .windowBackgroundColor))
        .onAppear { let saved = model.savedHost; host = saved; pairingCode = model.savedPairingCode(for: saved) }
    }
}
