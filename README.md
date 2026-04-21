# OPCUA-Server

OPCUA-Server is a Windows desktop simulator for exposing AutomationML/MTP-style
items as an OPC UA server. It lets you load an AML/XML file, inspect the parsed
OPC UA items, edit data types and initial values, then start a local OPC UA
endpoint that client tools can browse, read, write, and subscribe to.

The project is useful when you need a lightweight test server for validating
OPC UA clients, SCADA integrations, MTP/PEA data models, or demos without
connecting to real industrial hardware.

## What It Does

- Loads `.aml` or `.xml` files and extracts `ExternalInterface` definitions.
- Creates OPC UA variables from the parsed AML metadata.
- Hosts a local OPC UA server at `opc.tcp://localhost:4840`.
- Displays parsed nodes in a WPF grid before the server starts.
- Lets you edit each node's `DataType` and `InitialValue`.
- Supports basic read/write access control from the AML `Access` field.
- Provides a simple simulation loop that updates node values over time.

## Typical Use Cases

- Testing OPC UA clients such as UAExpert, Ignition, Kepware, or custom clients.
- Demonstrating a Process Equipment Assembly (PEA) or MTP-style address space.
- Validating that AML-exported OPC UA item metadata can be exposed as live nodes.
- Simulating changing process values before a real PLC, DCS, or skid is available.
- Checking client behavior for reads, writes, subscriptions, and data type handling.

## How It Works

1. The user clicks **Load AML** and selects an AML/XML file.
2. `AmlLoader` parses `ExternalInterface` elements from the file.
3. Parsed items are shown in the UI as editable `OpcUaItem` rows.
4. The user can fill or edit data types and initial values.
5. Clicking **Start Server** starts an OPC UA server using the OPC Foundation
   .NET Standard SDK.
6. `OpcNodeManager` creates variables under the `PEADevices` folder.
7. OPC UA clients connect to `opc.tcp://localhost:4840` and browse/read/write
   the generated nodes.
8. Clicking **Start Simulation** periodically changes variable values.

## AML Fields

The loader looks for `ExternalInterface` elements and reads child
`Attribute` values with these names:

| Attribute | Purpose |
| --- | --- |
| `Identifier` | OPC UA node identifier. |
| `Namespace` | Source namespace metadata from the AML file. |
| `Access` | Access level: `1` read, `2` write, `3` read/write. |
| `AttributeDataType` | Data type for the generated OPC UA variable. |
| `Value` | Initial value for the generated node. |

If fields are missing, the UI can fill sensible defaults before the server is
started.

## Supported Data Types

The UI currently supports:

- `Boolean`
- `Double`
- `Int16`
- `Int32`
- `Int64`
- `UInt32`
- `UInt64`
- `Byte`
- `DateTime`
- `String`

The server maps these values to OPC UA built-in data types and validates initial
values before startup.

## Simulation Behavior

The simulation loop updates values at the interval configured in the UI:

- Boolean values flip occasionally.
- Numeric values follow a bounded random walk.
- Date/time values update to the current UTC time.
- String values are updated with a simple timestamp heartbeat.

Simulation is optional. The server starts with the configured initial values and
does not automatically begin changing them.

## Getting Started

### Prerequisites

- Windows
- .NET 8 SDK
- Visual Studio 2022 or another editor that supports WPF projects
- Optional: UAExpert or another OPC UA client for testing

### Build

```powershell
dotnet restore
dotnet build
```

### Run

```powershell
dotnet run
```

Or open `App.sln` in Visual Studio and start the project.

## Using With UAExpert

1. Run the application.
2. Click **Load AML** and select an AML/XML file.
3. Review the parsed rows and adjust data types or initial values if needed.
4. Click **Start Server**.
5. Open UAExpert.
6. Add a server connection to:

```text
opc.tcp://localhost:4840
```

7. Browse `Objects -> PEADevices` to see the generated variables.
8. Read, subscribe, or write nodes depending on their access level.

## Security Notes

This project is configured for local development and simulation. The server
currently exposes a no-security endpoint, allows anonymous access, and
auto-accepts untrusted certificates.

Do not use this configuration as-is on a production network. For production or
shared environments, enable signed/encrypted endpoints, require authenticated
users, and manage trusted certificates explicitly.

## Project Structure

| File | Purpose |
| --- | --- |
| `MainWindow.xaml` | WPF user interface. |
| `MainWindow.xaml.cs` | UI event handlers, validation, and server controls. |
| `AmlLoader.cs` | AML/XML parser for extracting OPC UA item metadata. |
| `Models/OpcUaItems.cs` | Model representing a parsed OPC UA item. |
| `OpcUaServerHost.cs` | Programmatic OPC UA server configuration and lifecycle. |
| `SimpleServer.cs` | `StandardServer` wrapper that creates the node manager. |
| `OpcNodeManager.cs` | Address space creation, node writes, and simulation logic. |

## Limitations

- Intended for Windows because it is a WPF application.
- The default endpoint binds to localhost on port `4840`.
- The current security settings are development-only.
- Only scalar variable nodes are supported.
- The generated address space is flat under `PEADevices`.

## License

No license file is currently included in this repository.
