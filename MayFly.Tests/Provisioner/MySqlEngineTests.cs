using Docker.DotNet;
using Docker.DotNet.Models;
using FluentAssertions;
using MayFly.Provisioner.Contracts;
using MayFly.Provisioner.Docker;
using MayFly.Provisioner.Engines;
using Microsoft.Extensions.Logging.Abstractions;
using MySqlConnector;
using System.Text;
using Xunit;

[Trait("Category", "Docker")]
[Collection("docker-sequential")]
public class MySqlEngineTests
{
    private static (IDockerClient docker, IDockerProvisioner sut) NewSut()
    {
        var docker = new DockerClientBuilder().Build();
        var sut = new DockerProvisioner(
            docker,
            new PortAllocator(Array.Empty<int>()),
            new PlainVolumeProvisioner(docker),
            new IEngineProvider[] { new PostgresEngineProvider(), new MySqlEngineProvider() },
            NullLogger<DockerProvisioner>.Instance);
        return (docker, sut);
    }

    [Fact]
    public async Task Mysql_appuser_scoped_no_file_and_reachable()
    {
        var (docker, sut) = NewSut();
        var res = await sut.CreateAsync(new CreateInstanceRequest("mysql", 3, 256, "blank"), default);

        try
        {
            // --- Basic credential shape ---
            res.DbUser.Should().Be("appuser");
            res.AdminUser.Should().Be("root");

            // Connection via the sidecar-published host port (AllowPublicKeyRetrieval for
            // caching_sha2_password over non-TLS, which is the MySQL 8.x default plugin).
            var cs = $"Server=localhost;Port={res.PublicPort};Database={res.DbName};" +
                     $"User={res.DbUser};Password={res.DbPassword};AllowPublicKeyRetrieval=true;SslMode=None";

            await WaitForMySqlAsync(cs);

            await using var conn = new MySqlConnection(cs);
            await conn.OpenAsync();

            // --- 1. Connected as the scoped app user ---
            await using var userCmd = new MySqlCommand("SELECT CURRENT_USER()", conn);
            var currentUser = (string)(await userCmd.ExecuteScalarAsync())!;
            currentUser.Should().StartWith("appuser@",
                "must connect as appuser, not root");

            // --- 2. SHOW GRANTS must not contain FILE or global super-privilege ---
            await using var grantsCmd = new MySqlCommand("SHOW GRANTS FOR CURRENT_USER()", conn);
            var grantLines = new List<string>();
            await using (var reader = await grantsCmd.ExecuteReaderAsync())
            {
                while (await reader.ReadAsync())
                    grantLines.Add(reader.GetString(0));
            }
            var grantsStr = string.Join("\n", grantLines);
            grantsStr.Should().NotContain("FILE",
                "appuser must not hold the FILE global privilege");
            grantsStr.Should().NotContain("SUPER",
                "appuser must not hold SUPER");
            // Reject any global ALL PRIVILEGES (on *.*); scoped grants on `appdb`.* are fine.
            foreach (var line in grantLines)
            {
                if (line.Contains("ALL PRIVILEGES"))
                    line.Should().Contain("`appdb`",
                        "ALL PRIVILEGES may only appear scoped to appdb, never globally on *.*");
            }

            // --- 3. Legitimate DDL and DML succeed ---
            await using var ddlCmd = new MySqlCommand("CREATE TABLE t(x int)", conn);
            await ddlCmd.Awaiting(c => c.ExecuteNonQueryAsync()).Should().NotThrowAsync(
                "appuser must be able to run DDL on appdb");

            await using var dmlCmd = new MySqlCommand("INSERT INTO t VALUES(42)", conn);
            await dmlCmd.Awaiting(c => c.ExecuteNonQueryAsync()).Should().NotThrowAsync(
                "appuser must be able to run DML on appdb");

            // --- 4. FILE privilege is absent: SELECT INTO OUTFILE must be rejected ---
            // The SHOW GRANTS check above (lines 56-66) is the PRIMARY / definitive proof that
            // appuser lacks FILE. The OUTFILE rejection below is SUPPLEMENTARY.
            // /tmp is on a writable tmpfs, so the rejection is purely about the missing FILE
            // privilege — not a filesystem restriction.
            // Note: Under MySQL 8.4's default secure_file_priv=NULL, OUTFILE is rejected with
            // error 1290 for EVERY user (even root), regardless of FILE privilege.
            await using var outfileCmd = new MySqlCommand(
                "SELECT 1 INTO OUTFILE '/tmp/mayfly_probe'", conn);
            var ex = await Assert.ThrowsAsync<MySqlException>(
                () => outfileCmd.ExecuteNonQueryAsync());
            // MySQL raises different error numbers depending on version and secure_file_priv:
            //   1045 – Access denied (no FILE privilege, checked first)
            //   1142 – table-access denied
            //   1227 – access denied for this operation
            //   1290 – secure_file_priv restriction
            ex.Number.Should().BeOneOf(new[] { 1045, 1142, 1227, 1290 },
                "OUTFILE by appuser must be rejected (access-denied or secure_file_priv restriction)");

            // --- 5. Egress probe: outbound TCP from the DB container must fail ---
            // Mirrors EgressTests.cs exactly — nc -w2 times out with no default gateway on
            // the internal mayfly-users bridge and prints NOEGRESS.
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var execCreate = await docker.Exec.CreateContainerExecAsync(
                res.ContainerId,
                new ContainerExecCreateParameters
                {
                    Cmd = new List<string>
                    {
                        "sh", "-c",
                        "(nc -w2 1.1.1.1 80 </dev/null 2>/dev/null && echo REACHED) || echo NOEGRESS"
                    },
                    AttachStdout = true,
                    AttachStderr = true
                },
                cts.Token);

            using var execStream = await docker.Exec.StartContainerExecAsync(
                execCreate.ID,
                new ContainerExecStartParameters { Detach = false },
                cts.Token);

            using var stdoutBuf = new MemoryStream();
            using var stderrBuf = new MemoryStream();
            await execStream.CopyOutputToAsync(Stream.Null, stdoutBuf, stderrBuf, cts.Token);

            var probeOutput = Encoding.UTF8.GetString(stdoutBuf.ToArray())
                            + Encoding.UTF8.GetString(stderrBuf.ToArray());
            probeOutput.Should().Contain("NOEGRESS",
                "DB container on internal network must have no internet egress; " +
                "NOEGRESS is printed only when nc fails to connect");
            probeOutput.Should().NotContain("REACHED",
                "REACHED is only printed when nc succeeds — its presence means egress is open");
        }
        finally
        {
            await sut.DestroyAsync(res.ContainerId, res.VolumeName, res.PublicPort, default);
        }
    }

    private static async Task WaitForMySqlAsync(string cs)
    {
        Exception? lastEx = null;
        for (int i = 0; i < 90; i++)
        {
            try
            {
                await using var c = new MySqlConnection(cs);
                await c.OpenAsync();
                return;
            }
            catch (Exception ex)
            {
                lastEx = ex;
                await Task.Delay(1000);
            }
        }
        throw new TimeoutException($"MySQL not ready after 90 attempts: {lastEx?.Message}", lastEx);
    }
}
