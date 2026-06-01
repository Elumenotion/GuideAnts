# GuideAnts ROCm Setup on Linux

These instructions were tested on a fresh install of Ubuntu Desktop 26.04 running on a [Framework Desktop](https://frame.work/desktop) computer with an AMD AI Max+ 395 (Strix Halo) with 128GB of universal memory.

## 1. Install Docker Engine

The ROCm drivers do not work with Docker Desktop, so just use the Docker engine. Setup instructions [are here](https://docs.docker.com/engine/install/ubuntu/); [installing from the `apt` repository](https://docs.docker.com/engine/install/ubuntu/#install-using-the-repository) is the most straight-forward approach since Docker Desktop won't work.

## 2. Install ROCm drivers

```sh
sudo apt update
sudo apt install rocm
```

## 3. OPTIONAL Tools

### radeontop

You may find it helpful to install `radeontop` to display GPU usage:

```sh
sudo apt update
sudo apt install radeontop

sudo radeontop
```

The following should be installed if you wish to rebuild container images:

### .NET

```sh
sudo apt update
sudo apt install software-properties-common -y
```

Then install .NET Core; refer to the [RULES.md](../src/RULES.md) file for the correct .NET version. At the time of testing, the .NET version was 8.0.

```sh
sudo add-apt-repository ppa:dotnet/backports
sudo apt-get update
sudo apt-get install -y dotnet-sdk-8.0
```

### PowerShell

Install the latest version of PowerShell.

First, download the .deb file; the test was run using [`https://github.com/PowerShell/PowerShell/releases/download/v7.6.1/powershell_7.6.1-1.deb_amd64.deb`](`https://github.com/PowerShell/PowerShell/releases/download/v7.6.1/powershell_7.6.1-1.deb_amd64.deb`).

Then install it:

```sh
sudo dpkg -i powershell_7.5.6-1.deb_amd64.deb
sudo apt-get install -f
```

You can run PowerShell with the `pwsh` command.

### NodeJS

To allow for other versions, installation using Node Version Manager is recommended:

```sh
sudo curl -o- https://raw.githubusercontent.com/nvm-sh/nvm/v0.39.1/install.sh | bash
sudo nvm install 22.11
sudo nvm use 22.11
```

## 4. Run install script

Download or clone this repo and run the [start_linux.sh](../start_linux.sh) script. To do this, you'll need to add execute permission on the file.

```sh
chmod +x ./start_linux.sh
./start_linux.sh
```

