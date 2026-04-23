python -c "import PyPDF2; pdf = PyPDF2.PdfReader('/app/temp/Microsoft AI Cloud Partner Program benefits guide (1).pdf'); f = open('/app/temp/output.txt', 'w'); f.write('Number of pages: {}'.format(len(pdf.pages))); f.close()"

python -c "import matplotlib.pyplot as plt; plt.plot([1, 2, 3], [4, 5, 6]); plt.savefig('./test_plot.png')"

python -c "import torch; x = torch.tensor([1.0, 2.0, 3.0]); print(x * 2); print(torch.cuda.is_available())"

mkdir -p HelloApp && cd HelloApp && dotnet new console -n HelloApp && cd HelloApp && echo 'using System; namespace HelloApp { class Program { static void Main(string[] args) { Console.WriteLine("Hello World!"); } } }' > Program.cs && dotnet restore && dotnet build && dotnet run && cd ../.. && echo "Hello App has been created, built, and run successfully."

docker build -t python-3.11-dotnet-9-torch-cuda-user -f dockerfile.multi .

docker build -t python-3.11-dotnet-9-torch-cuda -f dockerfile .

docker run --gpus all -d python-3.11-dotnet-9-torch-cuda