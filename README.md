RustRelay9.0

RustRelay9.0 is a .NET 9.0 application that interfaces with Rust’s packet relay system, providing real-time inspection, visualization, and interaction with live Rust server data.

It acts as a relay host and inspection tool, enabling both server introspection and map/save manipulation through Rust’s network packets.

--------------------------------------------------
Overview
--------------------------------------------------

RustRelay9.0 connects to Rust’s packet relay endpoint and exposes detailed server state, map data, and entity/player information. It is designed to support live viewing, downloading, and editing of server data while the server is running.

Intended for:
- Rust server owners
- Tool and plugin developers
- Modding and analysis
- Visualization and inspection tools

--------------------------------------------------
Features
--------------------------------------------------

Map & World Data
- View map information
- 3D map viewer
- Download all parts of the map
- Inspect terrain, topology, and world metadata

Save Inspection & Editing
- View Rust save data
- Edit save contents
- Inspect serialized world state

Live Server State
- View currently connected players
- View active entities
- Real-time packet-based updates

Relay Integration
- Interfaces directly with Rust’s packet relay system
- High-performance async networking using .NET 9.0

--------------------------------------------------
Command-Line Arguments
--------------------------------------------------

The application is configured using command-line arguments.

Usage:
dotnet RustRelay9.0.dll --url <relay_url> --token <auth_token>

Arguments:
--url    Base URL of the Rust packet relay
--token  Authentication token for relay access

Example:
dotnet RustRelay9.0.dll --url http://+:8080 --token mySecretToken

On your rust server you must also include startup args.
-relayenabled -relayurl "http://serverIP:Port" -relaytoken "demotoken"

--------------------------------------------------
Requirements
--------------------------------------------------

- .NET 9.0 SDK
- Rust server with packet relay enabled
- Network access to the relay endpoint

--------------------------------------------------
Build & Run
--------------------------------------------------

Clone the repository:
git clone https://github.com/bmgjet/RustRelay9.0
cd RustRelay9.0

Build:
dotnet build

Run:
dotnet run -- --url <relay_url> --token <auth_token>

--------------------------------------------------
Planned Extensions
--------------------------------------------------

- Web-based UI dashboard
- Advanced 3D map rendering
- Entity filtering and search
- Snapshot and replay support
- Permission-based access control

