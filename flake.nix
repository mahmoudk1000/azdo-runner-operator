{
  description = "Azure DevOps Runners Operator";

  inputs = {
    nixpkgs.url = "nixpkgs/nixos-25.05";
    flake-utils.url = "github:numtide/flake-utils";
  };

  outputs =
    { nixpkgs, flake-utils, ... }:
    flake-utils.lib.eachDefaultSystem (
      system:
      let
        pkgs = import nixpkgs { inherit system; };
        dotnet-sdk = pkgs.dotnet-sdk_9;
        dotnet-runtime = pkgs.dotnet-runtime_9;
      in
      {
        packages = rec {
          default = pkgs.buildDotnetModule {
            inherit dotnet-sdk dotnet-runtime;

            pname = "AzDORunner";
            version = "0.1.0";
            src = ./.;
            projectFile = "AzDORunner.csproj";
            nugetDeps = ./nix/deps.nix;
            doCheck = true;
            meta = with pkgs.lib; {
              description = "Azure DevOps Runners Operator";
              homepage = "https://github.com/mahmoudk1000/azdo-runner-operator";
              license = licenses.mit;
              platforms = platforms.linux;
            };
          };

          docker = pkgs.dockerTools.buildImage {
            name = "mahmoudk1000/azdo-runner-operator";
            tag = "latest";
            contents = [ default ];
            config = {
              Cmd = [ "${default}/bin/AzDORunner" ];
              User = "0:0";
              WorkingDir = "/app";
              ExposedPorts = {
                "8080/tcp" = { };
              };
              Env = [
                "ASPNETCORE_URLS=http://+:8080"
                "DOTNET_RUNNING_IN_CONTAINER=true"
              ];
            };
          };
        };

        devShells.default = pkgs.mkShell {
          buildInputs = [
            dotnet-sdk
          ];

          shellHook = ''
            export DOTNET_CLI_TELEMETRY_OPTOUT=1
            export DOTNET_NOLOGO=1
            export ASPNETCORE_ENVIRONMENT=Development
          '';
        };
      }
    );
}
