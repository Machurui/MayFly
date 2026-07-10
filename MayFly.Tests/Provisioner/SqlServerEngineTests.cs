using Docker.DotNet;
using Docker.DotNet.Models;
using FluentAssertions;
using MayFly.Provisioner.Contracts;
using MayFly.Provisioner.Docker;
using MayFly.Provisioner.Engines;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text;
using Xunit;

[Trait("Category", "Docker")]
[Collection("docker-sequential")]
public class SqlServerEngineTests
{
    private static (IDockerClient docker, IDockerProvisioner sut) NewSut()
    {
        var docker = new DockerClientBuilder().Build();
        var sut = new DockerProvisioner(
            docker,
            new PortAllocator(Array.Empty<int>()),
            new PlainVolumeProvisioner(docker),
            new IEngineProvider[]
            {
                new PostgresEngineProvider(),
                new MySqlEngineProvider(),
                new MariaDbEngineProvider(),
                new SqlServerEngineProvider()
            },
            NullLogger<DockerProvisioner>.Instance);
        return (docker, sut);
    }

    // ARM64 host (Apple Silicon): mssql runs under amd64 emulation — boot is ~60-120s.
    // Fact timeout is 4 minutes to accommodate emulated boot + readiness polling (75×2s=150s).
    [Fact(Timeout = 240000)]
    public async Task Mssql_appuser_not_sysadmin_no_xp_cmdshell_and_reachable()
    {
        // Clean leaked containers from previous failed runs.
        await CleanLeakedContainersAsync();

        var (docker, sut) = NewSut();
        var res = await sut.CreateAsync(new CreateInstanceRequest("mssql", 3, 256, "blank"), default);

        try
        {
            // --- Basic credential shape ---
            res.DbUser.Should().Be("appuser");
            res.AdminUser.Should().Be("sa");
            res.DbName.Should().Be("appdb");

            // Wait for the DB to be reachable through the sidecar port.
            var cs = $"Server=localhost,{res.PublicPort};Database={res.DbName};" +
                     $"User Id={res.DbUser};Password={res.DbPassword};" +
                     $"TrustServerCertificate=True;Encrypt=True;";

            await WaitForSqlServerAsync(cs);

            await using var conn = new SqlConnection(cs);
            await conn.OpenAsync();

            // --- 1. appuser is NOT a sysadmin ---
            // IS_SRVROLEMEMBER returns 1 if member, 0 if not, null if unknown role.
            await using var roleCmd = new SqlCommand("SELECT IS_SRVROLEMEMBER('sysadmin')", conn);
            var isSysadmin = await roleCmd.ExecuteScalarAsync();
            Convert.ToInt32(isSysadmin).Should().Be(0,
                "appuser must NOT be a sysadmin — privilege escalation path must be closed");

            // --- 2. Legitimate DDL and DML succeed on appdb ---
            await using var ddlCmd = new SqlCommand("CREATE TABLE t(x int)", conn);
            await ddlCmd.Awaiting(c => c.ExecuteNonQueryAsync()).Should().NotThrowAsync(
                "appuser must be able to run DDL on appdb via db_ddladmin role");

            await using var dmlCmd = new SqlCommand("INSERT INTO t VALUES(1)", conn);
            await dmlCmd.Awaiting(c => c.ExecuteNonQueryAsync()).Should().NotThrowAsync(
                "appuser must be able to run DML on appdb via db_datawriter role");

            await using var selCmd = new SqlCommand("SELECT COUNT(*) FROM t", conn);
            var count = await selCmd.ExecuteScalarAsync();
            Convert.ToInt32(count).Should().Be(1,
                "appuser must be able to read back the inserted row via db_datareader role");

            // --- 3. xp_cmdshell is blocked (disabled + appuser is non-sysadmin) ---
            // xp_cmdshell is disabled by default in SQL Server; even if enabled, only sysadmins
            // (or those granted EXECUTE on xp_cmdshell explicitly) can run it.
            await using var shellCmd = new SqlCommand("EXEC xp_cmdshell 'whoami'", conn);
            await shellCmd.Awaiting(c => c.ExecuteNonQueryAsync())
                .Should().ThrowAsync<SqlException>(
                    "xp_cmdshell must throw — it is disabled by default and appuser is non-sysadmin");

            // --- 4. Egress probe: outbound TCP from DB container must fail ---
            // The mssql image is Ubuntu-based; nc/wget may not be present.
            // We use bash /dev/tcp (built into bash, no external tools required) with a
            // 3-second timeout to attempt a connection to 1.1.1.1:53.
            // On the internal mayfly-users network (Internal=true, no gateway), the connect
            // attempt stalls until timeout, bash exits non-zero → "NOEGRESS" is printed.
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var egressExec = await docker.Exec.CreateContainerExecAsync(
                res.ContainerId,
                new ContainerExecCreateParameters
                {
                    Cmd = new List<string>
                    {
                        "bash", "-c",
                        "timeout 3 bash -c 'echo > /dev/tcp/1.1.1.1/53' 2>/dev/null && echo REACHED || echo NOEGRESS"
                    },
                    AttachStdout = true,
                    AttachStderr = true
                },
                cts.Token);

            using var egressStream = await docker.Exec.StartContainerExecAsync(
                egressExec.ID,
                new ContainerExecStartParameters { Detach = false },
                cts.Token);

            using var egressStdout = new MemoryStream();
            using var egressStderr = new MemoryStream();
            await egressStream.CopyOutputToAsync(Stream.Null, egressStdout, egressStderr, cts.Token);

            var egressOutput = Encoding.UTF8.GetString(egressStdout.ToArray())
                             + Encoding.UTF8.GetString(egressStderr.ToArray());
            egressOutput.Should().Contain("NOEGRESS",
                "DB container on internal mayfly-users network must have no internet egress; " +
                "NOEGRESS is printed when /dev/tcp connection fails/times out");
            egressOutput.Should().NotContain("REACHED",
                "REACHED means egress is open — a security regression");
        }
        finally
        {
            await sut.DestroyAsync(res.ContainerId, res.VolumeName, res.PublicPort, default);
        }
    }

    private static async Task WaitForSqlServerAsync(string connectionString)
    {
        // mssql under emulation can take 60-120s to start accepting connections.
        // We poll for up to 150s (75 × 2s), mirroring the DockerProvisioner readiness loop.
        Exception? lastEx = null;
        for (int i = 0; i < 75; i++)
        {
            try
            {
                await using var c = new SqlConnection(connectionString);
                await c.OpenAsync();
                return;
            }
            catch (Exception ex)
            {
                lastEx = ex;
                await Task.Delay(2000);
            }
        }
        throw new TimeoutException(
            $"SQL Server not reachable via sidecar after 75 attempts (150 s): {lastEx?.Message}", lastEx);
    }

    private static async Task CleanLeakedContainersAsync()
    {
        try
        {
            using var docker = new DockerClientBuilder().Build();
            var containers = await docker.Containers.ListContainersAsync(
                new ContainersListParameters
                {
                    All = true,
                    Filters = new Dictionary<string, IDictionary<string, bool>>
                    {
                        ["name"] = new Dictionary<string, bool> { ["mayfly-"] = true }
                    }
                }, default);

            foreach (var c in containers)
            {
                try
                {
                    await docker.Containers.RemoveContainerAsync(
                        c.ID, new ContainerRemoveParameters { Force = true }, default);
                }
                catch { /* best-effort */ }
            }
        }
        catch { /* best-effort */ }
    }
}
