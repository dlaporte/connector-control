import ServiceManagement
import ConnectorControlState

/// Launch at login through SMAppService.
@MainActor
final class SMAppServiceAutostart: Autostart {
    var isEnabled: Bool { SMAppService.mainApp.status == .enabled }

    var requiresApproval: Bool { SMAppService.mainApp.status == .requiresApproval }

    func setEnabled(_ enabled: Bool) throws {
        if enabled {
            try SMAppService.mainApp.register()
        } else {
            try SMAppService.mainApp.unregister()
        }
    }
}
