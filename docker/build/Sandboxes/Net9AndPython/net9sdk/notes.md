docker build -t dotnet-9.0-python-3.11 -f dockerfile .

docker run -d -i --name dotnet-9.0-python-3.11-container dotnet-9.0-python-3.11