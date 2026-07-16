import SwiftUI
import ServiceManagement
import MCPEnablerCore

struct SettingsView: View {
    @EnvironmentObject var state: AppState

    var body: some View {
        Form {
            Toggle("Launch at login", isOn: Binding(
                get: { SMAppService.mainApp.status == .enabled },
                set: { on in
                    do {
                        if on { try SMAppService.mainApp.register() }
                        else { try SMAppService.mainApp.unregister() }
                    } catch {
                        state.lastError = "Launch at login needs the built app "
                            + "bundle (run scripts/build-app.sh): \(error.localizedDescription)"
                    }
                }))
        }
        .padding(20)
        .frame(width: 320)
    }
}
