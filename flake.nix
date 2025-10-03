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
        dotnet-sdk = pkgs.dotnetCorePackages.sdk_9_0;
        dotnet-runtime = pkgs.dotnetCorePackages.runtime_9_0;

        csproj = builtins.readFile ./AzDORunner.csproj;
        versionS = builtins.match ".*<Version>(.*?)</Version>.*" csproj;
        version = builtins.elemAt versionS 0;
      in
      {
        packages = rec {
          default = pkgs.buildDotnetModule {
            inherit dotnet-sdk dotnet-runtime version;

            pname = "AzDORunner";
            src = ./.;
            projectFile = "AzDORunner.csproj";
            nugetDeps = ./deps.json;
            doCheck = true;

            meta = with pkgs.lib; {
              mainProgram = "AzDORunner";
              description = "Azure DevOps Runners Operator";
              homepage = "https://github.com/mahmoudk1000/azdo-runner-operator";
              license = licenses.mit;
              platforms = platforms.linux;
            };
          };

          docker = pkgs.dockerTools.buildImage {
            name = "mahmoudk1000/azdo-runner-operator";
            tag = version;
            contents = [ default ];
            config = {
              Cmd = [ "${default}/bin/AzDORunner" ];
              User = "0:0";
              WorkingDir = "/app";
              ExposedPorts = {
                "443/tcp" = { };
              };
              Env = [
                "DOTNET_RUNNING_IN_CONTAINER=true"
                "KESTREL__ENDPOINTS__HTTPS__URL=https://0.0.0.0:443"
                "KESTREL__ENDPOINTS__HTTPS__CERTIFICATE__PATH=/certs/tls.crt"
                "KESTREL__ENDPOINTS__HTTPS__CERTIFICATE__KEYPATH=/certs/tls.key"
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
