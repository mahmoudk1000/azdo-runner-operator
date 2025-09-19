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
        projectFile = "AzDORunner.csproj";
        dotnet-sdk = pkgs.dotnet-sdk_9;
        dotnet-runtime = pkgs.dotnet-runtime_9;
        version = "0.1.0";
      in
      {
        packages = {
          default = pkgs.buildDotnetModule {
            inherit
              projectFile
              dotnet-sdk
              dotnet-runtime
              version
              ;
            pname = "AzDORunner";
            src = ./.;
            nugetDeps = ./nix/deps.nix;
            doCheck = true;
            meta = with pkgs.lib; {
              homepage = "github.com/mahmoudk1000/azdo-runner-operator";
              license = licenses.mit;
              platforms = platforms.linux;
            };
          };
        };

        devShells = {
          default = pkgs.mkShell {
            buildInputs = [ dotnet-sdk ] ++ (with pkgs; [ fantomas ]);
          };
        };
      }
    );
}
