namespace Jakar.OpenTelemetry.Contracts.Tests;

[TestFixture]
public sealed class ImageTagTests
{
	private static readonly Guid Id = Guid.Parse( "0192a6f1-3c4d-7e5f-8a9b-0c1d2e3f4a5b" );


	[Test]
	public void Create_GeneratesVersion7IdAndSanitizedName()
	{
		ImageTag tag = ImageTag.Create( @"C:\screens\error.png" );

		Assert.Multiple( () =>
						 {
							 Assert.That( tag.ID.Version,  Is.EqualTo( 7 ) );
							 Assert.That( tag.FileName,    Is.EqualTo( "error.png" ) );
							 Assert.That( ImageTag.Create( "a.png" ).ID, Is.Not.EqualTo( tag.ID ) );
						 } );
	}

	[Test]
	public void ToString_UsesPrefixNameAndDashedId() => Assert.That( new ImageTag( Id, "shot.png" ).ToString(), Is.EqualTo( "image:shot.png:0192a6f1-3c4d-7e5f-8a9b-0c1d2e3f4a5b" ) );


	[TestCase( "image:shot.png:0192a6f1-3c4d-7e5f-8a9b-0c1d2e3f4a5b",  "shot.png" )]
	[TestCase( "image:shot.png:0192A6F13C4D7E5F8A9B0C1D2E3F4A5B",      "shot.png" )]
	[TestCase( "image:a:b:c.png:0192a6f1-3c4d-7e5f-8a9b-0c1d2e3f4a5b", "a:b:c.png" )]
	[TestCase( "image:shot.png:0192a6f1-3c4d-7e5f-8a9b-0c1d2e3f4a5b  ", "shot.png" )]
	[TestCase( "  image:shot.png:0192a6f1-3c4d-7e5f-8a9b-0c1d2e3f4a5b", "shot.png" )]
	public void TryParse_ValidToken_ReturnsTag( string value, string expectedName )
	{
		bool parsed = ImageTag.TryParse( value, out ImageTag tag );

		Assert.Multiple( () =>
						 {
							 Assert.That( parsed,       Is.True );
							 Assert.That( tag.ID,       Is.EqualTo( Id ) );
							 Assert.That( tag.FileName, Is.EqualTo( expectedName ) );
						 } );
	}

	[TestCase( null )]
	[TestCase( "" )]
	[TestCase( "   " )]
	[TestCase( "shot.png:0192a6f1-3c4d-7e5f-8a9b-0c1d2e3f4a5b" )]
	[TestCase( "image::0192a6f1-3c4d-7e5f-8a9b-0c1d2e3f4a5b" )]
	[TestCase( "image:shot.png:not-a-guid" )]
	[TestCase( "x image:shot.png:0192a6f1-3c4d-7e5f-8a9b-0c1d2e3f4a5b" )]
	[TestCase( "image:shot.png:0192a6f1-3c4d-7e5f-8a9b-0c1d2e3f4a5b trailing" )]
	[TestCase( "image:shot.png:0192a6f1-3c4d-7e5f-8a9b-0c1d2e3f4a5b,image:b.png:0192a6f1-3c4d-7e5f-8a9b-0c1d2e3f4a5c" )]
	[TestCase( "image:shot.png:0192a6f13c4d-7e5f-8a9b0c1d2e3f4a5b" )] // hex groups with only some dashes: matches the pattern, but is not a GUID
	public void TryParse_InvalidToken_ReturnsFalse( string? value )
	{
		bool parsed = ImageTag.TryParse( value, out ImageTag tag );

		Assert.Multiple( () =>
						 {
							 Assert.That( parsed, Is.False );
							 Assert.That( tag,    Is.EqualTo( default(ImageTag) ) );
						 } );
	}


	[TestCase( null )]
	[TestCase( "" )]
	[TestCase( " \t" )]
	[TestCase( "no tags here" )]
	public void ParseAll_NothingToParse_ReturnsEmpty( string? value ) => Assert.That( ImageTag.ParseAll( value ), Is.Empty );

	[Test]
	public void ParseAll_SingleString_ReturnsTag() => Assert.That( ImageTag.ParseAll( "image:a.png:0192a6f1-3c4d-7e5f-8a9b-0c1d2e3f4a5b" ), Is.EqualTo( new[] { new ImageTag( Id, "a.png" ) } ) );

	[TestCase( "image:a.png:0192a6f1-3c4d-7e5f-8a9b-0c1d2e3f4a5b,image:b:c.png:0192a6f1-3c4d-7e5f-8a9b-0c1d2e3f4a5c" )]
	[TestCase( "image:a.png:0192a6f1-3c4d-7e5f-8a9b-0c1d2e3f4a5b;image:b:c.png:0192a6f1-3c4d-7e5f-8a9b-0c1d2e3f4a5c" )]
	[TestCase( """["image:a.png:0192a6f1-3c4d-7e5f-8a9b-0c1d2e3f4a5b","image:b:c.png:0192a6f1-3c4d-7e5f-8a9b-0c1d2e3f4a5c"]""" )]
	[TestCase( "['image:a.png:0192a6f1-3c4d-7e5f-8a9b-0c1d2e3f4a5b', 'image:b:c.png:0192a6f1-3c4d-7e5f-8a9b-0c1d2e3f4a5c']" )]
	[TestCase( "image:a.png:0192a6f1-3c4d-7e5f-8a9b-0c1d2e3f4a5b\nimage:b:c.png:0192a6f1-3c4d-7e5f-8a9b-0c1d2e3f4a5c" )]
	public void ParseAll_Containers_ReturnsEveryTagInOrder( string value )
	{
		List<ImageTag> tags = ImageTag.ParseAll( value );

		Assert.That( tags, Is.EqualTo( new[] { new ImageTag( Id, "a.png" ), new ImageTag( Guid.Parse( "0192a6f1-3c4d-7e5f-8a9b-0c1d2e3f4a5c" ), "b:c.png" ) } ) );
	}

	[Test]
	public void ParseAll_Duplicates_AreReturnedOnce() => Assert.That( ImageTag.ParseAll( "image:a.png:0192a6f1-3c4d-7e5f-8a9b-0c1d2e3f4a5b,image:a.png:0192A6F1-3C4D-7E5F-8A9B-0C1D2E3F4A5B" ), Has.Count.EqualTo( 1 ) );

	[Test]
	public void ParseAll_SkipsMatchesThatAreNotGuids() => Assert.That( ImageTag.ParseAll( "image:a.png:0192a6f13c4d-7e5f-8a9b0c1d2e3f4a5b,image:b.png:0192a6f1-3c4d-7e5f-8a9b-0c1d2e3f4a5b" ), Is.EqualTo( new[] { new ImageTag( Id, "b.png" ) } ) );

	[Test]
	public void ParseAll_IdFollowedByMoreHex_IsNotAMatch() => Assert.That( ImageTag.ParseAll( "image:a.png:0192a6f1-3c4d-7e5f-8a9b-0c1d2e3f4a5bff" ), Is.Empty );


	[TestCase( null,                    "image" )]
	[TestCase( "",                      "image" )]
	[TestCase( "   ",                   "image" )]
	[TestCase( "folder/",               "image" )]
	[TestCase( "  shot.png  ",          "shot.png" )]
	[TestCase( "/var/tmp/shot.png",     "shot.png" )]
	[TestCase( @"C:\tmp\shot.png",      "shot.png" )]
	[TestCase( "a/b\\c.png",            "c.png" )]
	[TestCase( "a\"b,c;d[e]f.png",      "a_b_c_d_e_f.png" )]
	[TestCase( "tab\there\u0001.png",   "tab_here_.png" )]
	[TestCase( "time 12:30.png",        "time 12:30.png" )]
	[TestCase( "bob's screenshot.png",  "bob_s screenshot.png" )]
	public void SanitizeFileName_ProducesSafeLeafName( string? input, string expected ) => Assert.That( ImageTag.SanitizeFileName( input ), Is.EqualTo( expected ) );

	[Test]
	public void SanitizeFileName_TruncatesToMaximumLength()
	{
		string sanitized = ImageTag.SanitizeFileName( new string( 'x', ImageTag.MAX_FILE_NAME_LENGTH + 50 ) );

		Assert.That( sanitized, Has.Length.EqualTo( ImageTag.MAX_FILE_NAME_LENGTH ) );
	}

	[TestCase( "plain.png" )]
	[TestCase( "with:colon.png" )]
	[TestCase( "bob's screenshot.png" )]
	[TestCase( "quote\"comma,semi;[bracket].png" )]
	[TestCase( "line\r\nbreak.png" )]
	[TestCase( "../../etc/passwd" )]
	[TestCase( "" )]
	public void Create_ThenToString_RoundTripsThroughTryParseAndParseAll( string fileName )
	{
		ImageTag tag = ImageTag.Create( fileName );

		Assert.Multiple( () =>
						 {
							 Assert.That( ImageTag.TryParse( tag.ToString(), out ImageTag parsed ), Is.True, tag.ToString() );
							 Assert.That( parsed,                                                   Is.EqualTo( tag ) );
							 Assert.That( ImageTag.ParseAll( $"[\"{tag}\",\"{ImageTag.Create( "other.png" )}\"]" ), Does.Contain( tag ) );
						 } );
	}
}
