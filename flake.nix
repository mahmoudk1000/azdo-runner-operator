{
  description = "Kubernetes Azure DevOps Runner Pool Autoscaler";

  inputs.nixpkgs.url = "nixpkgs/nixos-26.05";

  outputs =
    { self, nixpkgs }:
    let
      lastModifiedDate = self.lastModifiedDate or self.lastModified or "19700101";
      version = builtins.substring 0 8 lastModifiedDate;
      supportedSystems = [
        "x86_64-linux"
        "x86_64-darwin"
        "aarch64-linux"
        "aarch64-darwin"
      ];
      forAllSystems = nixpkgs.lib.genAttrs supportedSystems;
      nixpkgsFor = forAllSystems (system: import nixpkgs { inherit system; });
    in
    {
      packages = forAllSystems (
        system:
        let
          pkgs = nixpkgsFor.${system};
        in
        {
          default = pkgs.buildGoModule {
            pname = "kara";
            inherit version;
            src = ./.;
            subPackages = [
              "cmd/kara"
            ];
            vendorHash = "sha256-tKf1DkA4RAfmQA+4SSPi80BCV5bdFZCWbXuBzU4Ogdk=";
          };
        }
      );

      devShells = forAllSystems (
        system:
        let
          pkgs = nixpkgsFor.${system};
        in
        {
          default = pkgs.mkShell {
            buildInputs = with pkgs; [
              go
              go-tools
              golangci-lint
              gopls
              gotools
              kubebuilder
            ];
          };
        }
      );
    };
}
